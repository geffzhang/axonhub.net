using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the Responses API outbound transformer
/// (llm/transformer/openai/responses/outbound.go, outbound_convert.go).
/// Expectations mirror outbound_test.go.
/// </summary>
public sealed class ResponsesOutboundTransformerTests
{
    [Theory]
    [InlineData("", "key")]
    [InlineData("https://api.openai.com", "")]
    public void CreateRejectsEmptyApiKeyOrBaseUrl(string baseUrl, string apiKey)
    {
        var ex = Assert.Throws<ArgumentException>(() => ResponsesOutboundTransformer.Create(baseUrl, apiKey));
        Assert.Equal("apiKey or baseURL is empty", ex.Message);
    }

    private static ResponsesOutboundTransformer WebSocketTransformer()
        => new(new ResponsesOutboundConfig
        {
            BaseUrl = "https://api.openai.com/v1",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
            Transport = ResponsesOutboundTransformer.TransportWebSocket,
        });

    [Theory]
    [InlineData(RequestTypes.Compact)]
    [InlineData(RequestTypes.Image)]
    [InlineData("responses")]
    public void WebSocketTransportRejectsNonChatRequests(string requestType)
    {
        // outbound.go:214-220: only chat requests ride the WebSocket transport.
        var request = new LlmRequest
        {
            Model = "gpt-5",
            RequestType = requestType,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
        };

        var error = Assert.Throws<TransformerException>(
            () => WebSocketTransformer().TransformRequest(request));

        Assert.Equal(TransformerErrorKind.UnsupportedConversion, error.Kind);
        Assert.Equal(
            "unsupported conversion: WebSocket Responses requires a chat request",
            error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(RequestTypes.Chat)]
    public void WebSocketTransportAcceptsChatRequests(string? requestType)
    {
        var request = new LlmRequest
        {
            Model = "gpt-5",
            RequestType = requestType,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
        };

        var httpRequest = WebSocketTransformer().TransformRequest(request);

        Assert.Equal(ApiFormats.OpenAIResponseWebSocket, httpRequest.ApiFormat);
    }

    [Theory]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/custom", "https://api.openai.com/v1/custom")]
    public void NormalizesBaseUrl(string baseUrl, string expected)
    {
        var transformer = ResponsesOutboundTransformer.Create(baseUrl, "test-key");
        Assert.Equal(expected, transformer.BaseUrl);
    }

    [Theory]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/responses")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/responses")]
    [InlineData("https://api.openai.com/v1/custom", "https://api.openai.com/v1/custom/responses")]
    [InlineData("https://api.openai.com/custom#", "https://api.openai.com/custom/responses")]
    [InlineData("wss://chatgpt.com/backend-api/codex#", "wss://chatgpt.com/backend-api/codex/responses")]
    public void BuildsFullRequestUrl(string baseUrl, string expected)
    {
        var transformer = ResponsesOutboundTransformer.Create(baseUrl, "test-key");
        Assert.Equal(expected, transformer.BuildFullRequestUrl());
    }

    [Fact]
    public void TrimsDoubleHashAndUsesRawUrl()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://custom.example.com/v1##", "test-key");

        Assert.True(transformer.Config.RawUrl);
        Assert.Equal("https://custom.example.com/v1", transformer.BaseUrl);
        Assert.Equal("https://custom.example.com/v1", transformer.BuildFullRequestUrl());
        Assert.Equal("https://custom.example.com/v1", transformer.BuildCompactUrl());
    }

    [Fact]
    public void ReportsResponseApiFormat()
        => Assert.Equal(ApiFormats.OpenAIResponse, ResponsesOutboundTransformer.Create("https://api.openai.com", "k").ApiFormat);

    [Fact]
    public void TransformsSimpleTextRequest()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello, world!") }],
        });

        Assert.Equal("POST", httpReq.Method);
        Assert.Equal("https://api.openai.com/v1/responses", httpReq.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Accept"));
        Assert.NotNull(httpReq.Auth);
        Assert.Equal("bearer", httpReq.Auth!.Type);
        Assert.Equal("test-api-key", httpReq.Auth.ApiKey);
        Assert.Equal(ApiFormats.OpenAIResponse, httpReq.ApiFormat);
        Assert.True(httpReq.SkipInboundQueryMerge);
        Assert.Null(httpReq.Metadata);
        Assert.Equal(string.Empty, httpReq.RequestType);
        Assert.NotNull(httpReq.TransformerMetadata);

        using var body = JsonDocument.Parse(httpReq.Body!);
        Assert.Equal("gpt-4o", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello, world!", body.RootElement.GetProperty("input").GetString());
        Assert.Equal(string.Empty, body.RootElement.GetProperty("instructions").GetString());
        Assert.False(body.RootElement.TryGetProperty("parallel_tool_calls", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void OmitsTemperatureEvenWhenTheUnifiedRequestCarriesIt()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            Temperature = 0.7,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
        });

        using var body = JsonDocument.Parse(httpReq.Body!);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void MapsSystemMessageToInstructions()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            Messages =
            [
                new Message { Role = "system", Content = MessageContent.FromText("You are a helpful assistant.") },
                new Message { Role = "user", Content = MessageContent.FromText("Hello!") },
            ],
        });

        var payload = LlmJson.Deserialize<ResponsesRequest>(httpReq.Body!)!;
        Assert.Equal("You are a helpful assistant.", payload.Instructions);

        // With a system message present the request takes the array form; the
        // system message itself is carried in instructions only.
        Assert.NotNull(payload.Input);
        Assert.Null(payload.Input!.Text);
        var item = Assert.Single(payload.Input.Items!);
        Assert.Equal("message", item.Type);
        Assert.Equal("user", item.Role);
        Assert.Equal("Hello!", item.Content!.Items![0].Text);
    }

    [Fact]
    public void ConvertsImageGenerationToolAndStoresOutputFormat()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var request = new LlmRequest
        {
            Model = "gpt-4o",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Generate a cat") }],
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.ImageGeneration,
                    ImageGeneration = new ImageGeneration
                    {
                        Quality = "high",
                        Size = "1024x1024",
                        OutputFormat = "png",
                        OutputCompression = 80,
                    },
                },
            ],
        };

        var httpReq = transformer.TransformRequest(request);
        var payload = LlmJson.Deserialize<ResponsesRequest>(httpReq.Body!)!;
        var tool = Assert.Single(payload.Tools!);
        Assert.Equal(ToolTypes.ImageGeneration, tool.Type);
        Assert.Equal("high", tool.Quality);
        Assert.Equal("1024x1024", tool.Size);
        Assert.Equal("png", tool.OutputFormat);
        Assert.Equal(80, tool.OutputCompression);
        Assert.Equal("png", request.TransformerMetadata!["image_output_format"].GetString());
    }

    [Fact]
    public void ConvertsWebSearchAndGoogleSearchTools()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("news") }],
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.WebSearch,
                    WebSearch = new WebSearch
                    {
                        AllowedDomains = ["openai.com"],
                        UserLocation = new WebSearchToolUserLocation { Type = "approximate", Country = "US" },
                    },
                },
                new Tool
                {
                    Type = ToolTypes.GoogleSearch,
                    Google = new GoogleTools { Search = new GoogleSearch() },
                },
            ],
        });

        var payload = LlmJson.Deserialize<ResponsesRequest>(httpReq.Body!)!;
        Assert.NotNull(payload.Tools);
        Assert.Equal(2, payload.Tools!.Count);
        Assert.Equal(ToolTypes.WebSearch, payload.Tools[0].Type);
        Assert.Equal(new List<string> { "openai.com" }, payload.Tools[0].Filters!.AllowedDomains);
        Assert.Equal("approximate", payload.Tools[0].UserLocation!.Type);
        Assert.Equal("US", payload.Tools[0].UserLocation!.Country);
        Assert.Equal(ToolTypes.WebSearch, payload.Tools[1].Type);
    }

    [Fact]
    public void SkipsUnsupportedToolTypes()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
            Tools = [new Tool { Type = "unsupported_tool" }],
        });

        using var body = JsonDocument.Parse(httpReq.Body!);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public void NormalizesStrictFunctionToolSchemas()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        using var parameters = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"string\"},\"a\":{\"type\":\"string\"}},"
            + "\"required\":[\"c\"],\"additionalProperties\":true}");

        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
            Tools =
            [
                new Tool
                {
                    Type = "function",
                    Function = new Function
                    {
                        Name = "strict_tool",
                        Strict = true,
                        Parameters = parameters.RootElement.Clone(),
                    },
                },
            ],
        });

        var payload = LlmJson.Deserialize<ResponsesRequest>(httpReq.Body!)!;
        var tool = Assert.Single(payload.Tools!);
        Assert.True(tool.Strict);
        Assert.NotNull(tool.Parameters);
        Assert.False(tool.Parameters!["additionalProperties"].GetBoolean());

        // The reference appends missing keys while iterating a Go map, so its
        // order is random; the port uses the schema's document order
        // (difference C065).
        Assert.Equal(
            new List<string> { "c", "b", "a" },
            tool.Parameters!["required"].EnumerateArray().Select(static e => e.GetString()!).ToList());
    }

    [Fact]
    public void PreservesToolSchemaNumberLexemes()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        using var parameters = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"amount\":{\"type\":\"number\","
            + "\"minimum\":1.0,\"maximum\":1e3,\"multipleOf\":123456789012345678901}}}");

        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
            Tools =
            [
                new Tool
                {
                    Type = "function",
                    Function = new Function
                    {
                        Name = "numeric_schema",
                        Parameters = parameters.RootElement.Clone(),
                    },
                },
            ],
        });

        var payload = LlmJson.Deserialize<ResponsesRequest>(httpReq.Body!)!;
        var amount = Assert.Single(payload.Tools!).Parameters!["properties"].GetProperty("amount");

        // Go decodes parameters into map[string]any (numbers become float64 and
        // are re-encoded, e.g. 1.0 -> 1); the port keeps the original lexemes
        // (difference C066).
        Assert.Equal("1.0", amount.GetProperty("minimum").GetRawText());
        Assert.Equal("1e3", amount.GetProperty("maximum").GetRawText());
        Assert.Equal("123456789012345678901", amount.GetProperty("multipleOf").GetRawText());
    }

    [Fact]
    public void ConvertsNamedImageGenerationToolChoiceWithoutName()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-5.5",
            RequestType = RequestTypes.Chat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Generate an image.") }],
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.ImageGeneration,
                    ImageGeneration = new ImageGeneration { Model = "gpt-image-2" },
                },
            ],
            ToolChoice = new ToolChoice
            {
                NamedToolChoice = new NamedToolChoice { Type = ToolTypes.ImageGeneration },
            },
        });

        using var body = JsonDocument.Parse(httpReq.Body!);
        var choice = body.RootElement.GetProperty("tool_choice");
        Assert.Equal(ToolTypes.ImageGeneration, choice.GetProperty("type").GetString());
        Assert.False(choice.TryGetProperty("name", out _));

        var tool = body.RootElement.GetProperty("tools")[0];
        Assert.Equal(ToolTypes.ImageGeneration, tool.GetProperty("type").GetString());
        Assert.Equal("gpt-image-2", tool.GetProperty("model").GetString());
    }

    [Fact]
    public void KeepsWebSearchRequiredToolChoiceAsString()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o-search-preview",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("latest ai news") }],
            Tools = [new Tool { Type = ToolTypes.WebSearch }],
            ToolChoice = new ToolChoice { ToolChoiceMode = "required" },
        });

        using var body = JsonDocument.Parse(httpReq.Body!);
        var choice = body.RootElement.GetProperty("tool_choice");
        Assert.Equal(JsonValueKind.String, choice.ValueKind);
        Assert.Equal("required", choice.GetString());
    }

    [Fact]
    public void AggregatesNamespaceTools()
    {
        var inboundRequest = JsonRequest(
            """
            {
              "model": "gpt-4o",
              "input": "List the projects.",
              "tools": [
                {
                  "type": "namespace",
                  "name": "mcp__codebase_memory_mcp",
                  "tools": [
                    {"type": "function", "name": "list_projects", "parameters": {"type": "object"}},
                    {"type": "function", "name": "get_project", "parameters": {"type": "object"}}
                  ]
                },
                {"type": "function", "name": "get_weather", "parameters": {"type": "object"}}
              ]
            }
            """);

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        Assert.Equal(3, llmRequest.Tools!.Count);

        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(llmRequest);

        using var body = JsonDocument.Parse(httpReq.Body!);
        var tools = body.RootElement.GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());

        var namespaceTool = tools[0];
        Assert.Equal("namespace", namespaceTool.GetProperty("type").GetString());
        Assert.Equal("mcp__codebase_memory_mcp", namespaceTool.GetProperty("name").GetString());
        Assert.Equal(2, namespaceTool.GetProperty("tools").GetArrayLength());

        Assert.Equal("get_weather", tools[1].GetProperty("name").GetString());
    }

    [Fact]
    public void ReplaysProviderRawToolsAndToolChoice()
    {
        var inboundRequest = JsonRequest(
            """
            {
              "model": "gpt-4o",
              "input": "Search and run shell.",
              "tools": [
                {"type": "tool_search", "name": "search_docs", "namespace": "docs"},
                {"type": "function", "name": "get_weather", "parameters": {"type": "object", "properties": {}}}
              ],
              "tool_choice": {
                "type": "tool_search",
                "tools": [{"type": "tool_search", "name": "search_docs"}]
              }
            }
            """);

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        llmRequest.Model = "mapped-model";

        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(llmRequest);

        using var body = JsonDocument.Parse(httpReq.Body!);
        Assert.Equal("mapped-model", body.RootElement.GetProperty("model").GetString());

        var tools = body.RootElement.GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.Equal("tool_search", tools[0].GetProperty("type").GetString());
        Assert.Equal("docs", tools[0].GetProperty("namespace").GetString());

        var toolChoice = body.RootElement.GetProperty("tool_choice");
        Assert.Equal("tool_search", toolChoice.GetProperty("type").GetString());
        Assert.Equal(1, toolChoice.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public void DropsRawToolChoiceWhenNoToolsAreSent()
    {
        var inboundRequest = JsonRequest(
            """
            {
              "model": "gpt-4o",
              "input": "Search the docs.",
              "tool_choice": {
                "type": "tool_search",
                "tools": [{"type": "tool_search", "name": "search_docs"}]
              }
            }
            """);

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        var httpRequest = ResponsesOutboundTransformer
            .Create("https://api.openai.com", "test-api-key")
            .TransformRequest(llmRequest);

        using var body = JsonDocument.Parse(httpRequest.Body!);
        Assert.False(body.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public void KeepsToolChoiceWhenRawToolsAreRestored()
    {
        var inboundRequest = JsonRequest(
            """
            {
              "model": "gpt-4o",
              "input": "Search the docs.",
              "tools": [{"type": "tool_search", "name": "search_docs"}],
              "tool_choice": {
                "type": "tool_search",
                "tools": [{"type": "tool_search", "name": "search_docs"}]
              }
            }
            """);

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        var httpRequest = ResponsesOutboundTransformer
            .Create("https://api.openai.com", "test-api-key")
            .TransformRequest(llmRequest);

        using var body = JsonDocument.Parse(httpRequest.Body!);
        Assert.True(body.RootElement.TryGetProperty("tools", out var tools));
        Assert.True(tools.GetArrayLength() > 0);
        Assert.True(body.RootElement.TryGetProperty("tool_choice", out var choice));
        Assert.Equal("tool_search", choice.GetProperty("type").GetString());
    }

    [Fact]
    public void ReplaysRawOnlyInputItems()
    {
        var inboundRequest = JsonRequest(
            """
            {
              "model": "gpt-4o",
              "input": [
                {
                  "type": "tool_search_call",
                  "call_id": "call_search",
                  "status": "completed",
                  "arguments": {"query": "image generation", "limit": 10}
                },
                {
                  "type": "message",
                  "role": "user",
                  "content": [{"type": "input_text", "text": "hello"}]
                }
              ]
            }
            """);

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(llmRequest);

        using var body = JsonDocument.Parse(httpReq.Body!);
        var input = body.RootElement.GetProperty("input");
        Assert.Equal(JsonValueKind.Array, input.ValueKind);
        Assert.Equal(2, input.GetArrayLength());

        var rawItem = input[0];
        Assert.Equal("tool_search_call", rawItem.GetProperty("type").GetString());
        var arguments = rawItem.GetProperty("arguments");
        Assert.Equal("image generation", arguments.GetProperty("query").GetString());
        Assert.Equal(10, arguments.GetProperty("limit").GetInt64());

        Assert.Equal("message", input[1].GetProperty("type").GetString());
    }

    [Fact]
    public void DoesNotReplayRawToolWhenToolsChanged()
    {
        var inboundRequest = JsonRequest(
            """
            {
              "model": "gpt-4o",
              "input": "Search and run shell.",
              "tools": [
                {"type": "tool_search", "name": "search_docs", "namespace": "docs"},
                {"type": "function", "name": "get_weather", "parameters": {"type": "object", "properties": {}}}
              ]
            }
            """);

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        using var parameters = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        llmRequest.Tools =
        [
            new Tool
            {
                Type = "function",
                Function = new Function
                {
                    Name = "different_tool",
                    Parameters = parameters.RootElement.Clone(),
                },
            },
        ];

        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(llmRequest);

        using var body = JsonDocument.Parse(httpReq.Body!);
        var tools = body.RootElement.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("different_tool", tools[0].GetProperty("name").GetString());
    }

    [Fact]
    public void PreservesClientMetadata()
    {
        var inboundRequest = JsonRequest(
            """
            {
              "model": "gpt-5",
              "input": "hello",
              "stream": true,
              "client_metadata": {"x-codex-turn-state": "ts-1", "custom": "kept"}
            }
            """);

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        llmRequest.RawRequest = inboundRequest;

        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(llmRequest);

        using var body = JsonDocument.Parse(httpReq.Body!);
        var metadata = body.RootElement.GetProperty("client_metadata");
        Assert.Equal("ts-1", metadata.GetProperty("x-codex-turn-state").GetString());
        Assert.Equal("kept", metadata.GetProperty("custom").GetString());
    }

    [Fact]
    public void SetsParallelToolCallsFalseForResponsesLite()
    {
        var inboundRequest = JsonRequest("{\"model\":\"gpt-4o\",\"input\":\"hi\"}");
        inboundRequest.Headers![ResponsesKeys.ResponsesLiteHeader] = ["true"];

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        llmRequest.RawRequest = inboundRequest;

        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(llmRequest);

        using var body = JsonDocument.Parse(httpReq.Body!);
        Assert.True(body.RootElement.TryGetProperty("parallel_tool_calls", out var parallel));
        Assert.False(parallel.GetBoolean());
    }

    [Fact]
    public void OmitsParallelToolCallsWhenThereAreNoTools()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            ParallelToolCalls = true,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
        });

        using var body = JsonDocument.Parse(httpReq.Body!);
        Assert.False(body.RootElement.TryGetProperty("parallel_tool_calls", out _));
    }

    [Fact]
    public void FallsBackToSharedSessionIdWithConversationAnchor()
    {
        var transformer = NewSessionScopedTransformer("shared-session-123");
        var request = new LlmRequest
        {
            Model = "gpt-5.4",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

        var httpReq = transformer.TransformRequest(request);
        var payload = LlmJson.Deserialize<ResponsesRequest>(httpReq.Body!)!;

        var expected = "shared-session-123-" + ResponsesCacheAnchor.ConversationAnchor(request.Messages);
        Assert.Equal(expected, payload.PromptCacheKey);
    }

    [Fact]
    public void ScopesSessionFallbackPerConversation()
    {
        var transformer = NewSessionScopedTransformer("shared-session-123");

        static LlmRequest NewRequest(string firstUser, params Message[] extraTurns)
        {
            List<Message> messages =
            [
                new() { Role = "system", Content = MessageContent.FromText("You are an agent.") },
                new() { Role = "user", Content = MessageContent.FromText(firstUser) },
            ];
            messages.AddRange(extraTurns);
            return new LlmRequest { Model = "gpt-5.4", Messages = messages };
        }

        string CacheKey(LlmRequest request)
        {
            var httpReq = transformer.TransformRequest(request);
            var payload = LlmJson.Deserialize<ResponsesRequest>(httpReq.Body!)!;
            Assert.NotNull(payload.PromptCacheKey);
            return payload.PromptCacheKey!;
        }

        // Later turns of the same conversation keep the same cache key.
        var turn1 = CacheKey(NewRequest("task A"));
        var turn2 = CacheKey(NewRequest(
            "task A",
            new Message { Role = "assistant", Content = MessageContent.FromText("working") },
            new Message { Role = "user", Content = MessageContent.FromText("continue") }));
        Assert.Equal(turn1, turn2);

        // Sibling conversations in the same session get distinct cache keys.
        Assert.NotEqual(turn1, CacheKey(NewRequest("task B")));

        // Client-provided keys are preserved untouched.
        var explicitKey = NewRequest("task A");
        explicitKey.PromptCacheKey = "client-key";
        Assert.Equal("client-key", CacheKey(explicitKey));

        // A large shared instruction prefix must not starve the first user
        // message out of the fingerprint: sibling conversations still get
        // distinct keys.
        var largeSystem = string.Concat(Enumerable.Repeat("shared instructions. ", 2048));

        string LargeKey(string firstUser) => CacheKey(new LlmRequest
        {
            Model = "gpt-5.4",
            Messages =
            [
                new Message { Role = "system", Content = MessageContent.FromText(largeSystem) },
                new Message { Role = "user", Content = MessageContent.FromText(firstUser) },
            ],
        });

        Assert.NotEqual(LargeKey("task A"), LargeKey("task B"));

        // Non-text content contributes to the fingerprint: first user messages
        // that differ only by an image part get distinct keys.
        string ImageKey(string imageUrl) => CacheKey(new LlmRequest
        {
            Model = "gpt-5.4",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromParts(
                    [
                        new MessageContentPart { Type = "text", Text = "describe this image" },
                        new MessageContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new ImageUrl { Url = imageUrl },
                        },
                    ]),
                },
            ],
        });

        Assert.NotEqual(
            ImageKey("https://example.com/a.png"),
            ImageKey("https://example.com/b.png"));
    }

    [Fact]
    public void ConversationAnchorIsEmptyWithoutQualifyingMessages()
    {
        Assert.Equal(string.Empty, ResponsesCacheAnchor.ConversationAnchor(null));
        Assert.Equal(string.Empty, ResponsesCacheAnchor.ConversationAnchor([]));
        Assert.Equal(
            string.Empty,
            ResponsesCacheAnchor.ConversationAnchor(
                [new Message { Role = "assistant", Content = MessageContent.FromText("hi") }]));
    }

    [Fact]
    public void ConversationAnchorIsSixteenLowercaseHexCharacters()
    {
        var anchor = ResponsesCacheAnchor.ConversationAnchor(
            [new Message { Role = "user", Content = MessageContent.FromText("hi") }]);

        Assert.Equal(16, anchor.Length);
        Assert.All(anchor, static c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void TransformResponseRejectsNil()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var ex = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(null));
        Assert.Equal("http response is nil", ex.Message);
    }

    [Fact]
    public void TransformResponseReportsHttpError()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var ex = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            new HttpResponseData { StatusCode = 400, Body = Bytes("{\"error\":{\"message\":\"Bad request\"}}") }));

        Assert.Equal("HTTP error 400: {\"error\":{\"message\":\"Bad request\"}}", ex.Message);
    }

    [Fact]
    public void TransformResponseRejectsEmptyBody()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var ex = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            new HttpResponseData { StatusCode = 200, Body = [] }));

        Assert.Equal("response body is empty", ex.Message);
    }

    [Fact]
    public void TransformResponseReportsSyntaxError()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var ex = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            new HttpResponseData { StatusCode = 200, Body = Bytes("{invalid json}") }));

        Assert.StartsWith(
            "failed to unmarshal responses api response: invalid character",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransformsTextOutput()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var result = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Bytes(
                """
                {
                  "id": "resp_123",
                  "object": "response",
                  "created_at": 1759161016,
                  "status": "completed",
                  "model": "gpt-4o",
                  "output": [
                    {
                      "id": "msg_123",
                      "type": "message",
                      "status": "completed",
                      "content": [{"type": "output_text", "text": "Hello! How can I help you?"}],
                      "role": "assistant"
                    }
                  ],
                  "usage": {"input_tokens": 10, "output_tokens": 20, "total_tokens": 30}
                }
                """),
        });

        Assert.Equal("chat.completion", result.Object);
        Assert.Equal("resp_123", result.Id);
        Assert.Equal("gpt-4o", result.Model);
        Assert.Equal(1759161016, result.Created);
        var choice = Assert.Single(result.Choices);
        Assert.Equal("assistant", choice.Message!.Role);
        Assert.Equal("Hello! How can I help you?", choice.Message.Content!.Content);
        Assert.Equal("stop", choice.FinishReason);
        Assert.NotNull(result.Usage);
        Assert.Equal(10, result.Usage!.PromptTokens);
        Assert.Equal(20, result.Usage.CompletionTokens);
        Assert.Equal(30, result.Usage.TotalTokens);
    }

    [Fact]
    public void TransformsNullOutputItemAsZeroItem()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var result = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Bytes(
                """
                {
                  "id": "resp_null",
                  "object": "response",
                  "created_at": 1759161016,
                  "status": "completed",
                  "model": "gpt-4o",
                  "output": [
                    null,
                    {
                      "id": "msg_123",
                      "type": "message",
                      "content": [{"type": "output_text", "text": "hi"}],
                      "role": "assistant"
                    }
                  ]
                }
                """),
        });

        var choice = Assert.Single(result.Choices);
        Assert.Equal("hi", choice.Message!.Content!.Content);
        Assert.Equal("stop", choice.FinishReason);
    }

    [Fact]
    public void TransformsImageGenerationOutput()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var result = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Bytes(
                """
                {
                  "id": "resp_456",
                  "object": "response",
                  "created_at": 1759161016,
                  "status": "completed",
                  "model": "gpt-4o",
                  "output": [
                    {
                      "id": "img_123",
                      "type": "image_generation_call",
                      "status": "completed",
                      "result": "data:image/png;base64,iVBORw0KGgo="
                    }
                  ]
                }
                """),
        });

        var content = Assert.Single(result.Choices).Message!.Content!;
        var part = Assert.Single(content.MultipleContent!);
        Assert.Equal("image_url", part.Type);
        Assert.NotNull(part.ImageUrl);
        Assert.Contains("data:image/png;base64,", part.ImageUrl!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformsEncryptedReasoning()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var result = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Bytes(
                """
                {
                  "id": "resp_789",
                  "object": "response",
                  "created_at": 1759161016,
                  "status": "completed",
                  "model": "gpt-4o",
                  "output": [
                    {"id": "rs_123", "type": "reasoning", "summary": [], "encrypted_content": "encrypted_data_here"}
                  ]
                }
                """),
        });

        Assert.Equal("encrypted_data_here", Assert.Single(result.Choices).Message!.ReasoningSignature);
    }

    [Fact]
    public void TransformsPreviousResponseId()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var result = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Bytes(
                """
                {
                  "id": "resp_456",
                  "object": "response",
                  "created_at": 1759161016,
                  "status": "completed",
                  "model": "gpt-5.4",
                  "previous_response_id": "resp_prev_123",
                  "output": [
                    {
                      "id": "msg_456",
                      "type": "message",
                      "status": "completed",
                      "content": [{"type": "output_text", "text": "Continued response"}],
                      "role": "assistant"
                    }
                  ]
                }
                """),
        });

        Assert.Equal("resp_prev_123", result.PreviousResponseId);
    }

    [Theory]
    [InlineData("completed", "stop")]
    [InlineData("failed", "error")]
    [InlineData("incomplete", "length")]
    [InlineData("canceled", "cancelled")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("in_progress", null)]
    public void MapsStatusToFinishReason(string status, string? expectedFinishReason)
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var result = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Bytes(
                "{\"id\":\"resp_status\",\"object\":\"response\",\"created_at\":1700000000,"
                + "\"status\":\"" + status + "\",\"model\":\"gpt-5\","
                + "\"output\":[{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\","
                + "\"content\":[{\"type\":\"output_text\",\"text\":\"x\"}]}]}"),
        });

        Assert.Equal(expectedFinishReason, Assert.Single(result.Choices).FinishReason);
    }

    [Fact]
    public void ToolCallsTakePrecedenceOverStatus()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var result = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Bytes(
                """
                {
                  "id": "resp_tool",
                  "object": "response",
                  "created_at": 1700000000,
                  "status": "incomplete",
                  "model": "gpt-5",
                  "output": [
                    {
                      "id": "fc_1",
                      "type": "function_call",
                      "call_id": "call_1",
                      "name": "get_weather",
                      "arguments": "{\"city\":\"SF\"}"
                    }
                  ]
                }
                """),
        });

        Assert.Equal("tool_calls", Assert.Single(result.Choices).FinishReason);
    }

    [Fact]
    public void TransformsUsageDetails()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var result = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Bytes(
                """
                {
                  "id": "resp_usage",
                  "object": "response",
                  "created_at": 1700000000,
                  "status": "completed",
                  "model": "gpt-5",
                  "output": [],
                  "usage": {
                    "input_tokens": 10,
                    "input_tokens_details": {"cached_tokens": 3, "cache_write_tokens": 4},
                    "output_tokens": 20,
                    "output_tokens_details": {"reasoning_tokens": 5},
                    "total_tokens": 30
                  }
                }
                """),
        });

        Assert.NotNull(result.Usage);
        Assert.Equal(10, result.Usage!.PromptTokens);
        Assert.Equal(20, result.Usage.CompletionTokens);
        Assert.Equal(30, result.Usage.TotalTokens);
        Assert.Equal(3, result.Usage.PromptTokensDetails!.CachedTokens);
        Assert.Equal(4, result.Usage.PromptTokensDetails.WriteCachedTokens);
        Assert.Equal(5, result.Usage.CompletionTokensDetails!.ReasoningTokens);
    }

    [Fact]
    public void TransformErrorParsesUpstreamEnvelope()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var error = transformer.TransformError(new HttpClientError(
            "POST",
            "https://api.openai.com/v1/responses",
            400,
            "400 Bad Request",
            Bytes("{\"error\":{\"code\":\"bad\",\"message\":\"Bad request\",\"type\":\"invalid_request_error\",\"param\":\"p\"}}")));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("bad", error.Detail.Code);
        Assert.Equal("Bad request", error.Detail.Message);
        Assert.Equal("invalid_request_error", error.Detail.Type);
        Assert.Equal("p", error.Detail.Param);
    }

    [Fact]
    public void TransformErrorFallsBackToTrimmedBody()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var error = transformer.TransformError(new HttpClientError(
            "POST", "https://api.openai.com/v1/responses", 500, "500 Internal Server Error", Bytes("  upstream boom  ")));

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("upstream boom", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void TransformErrorFallsBackWhenMessageIsEmpty()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var error = transformer.TransformError(new HttpClientError(
            "POST", "https://api.openai.com/v1/responses", 502, "502 Bad Gateway", Bytes("{\"error\":{\"message\":\"\"}}")));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("{\"error\":{\"message\":\"\"}}", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void TransformErrorForNilErrorIsInternal()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
        var error = transformer.TransformError(null);

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Internal Server Error", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    private static ResponsesOutboundTransformer NewSessionScopedTransformer(string sessionId)
        => new(new ResponsesOutboundConfig
        {
            BaseUrl = "https://api.openai.com",
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
            SessionIdProvider = () => sessionId,
        });

    private static HttpRequestData JsonRequest(string body) => new()
    {
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
        Body = Bytes(body),
    };

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
