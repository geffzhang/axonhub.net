using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Copilot;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for llm/transformer/openai/copilot/outbound.go: the Copilot
/// editor headers, the GPT-5+/Grok Responses API routing, the response-envelope
/// unwrap, the stream call-id repair and the error envelope. Ported from
/// copilot/outbound_test.go; the reference spins an httptest server and inspects
/// the captured request, the port asserts the same method/path/headers/auth on
/// the built <see cref="HttpRequestData"/>.
/// </summary>
public sealed class CopilotOutboundTransformerTests
{
    private const string MockToken = "ghu_testtoken123";

    private sealed class MockTokenProvider : ICopilotTokenProvider
    {
        public string Token { get; init; } = string.Empty;

        public Exception? Error { get; init; }

        public string GetToken(CancellationToken cancellationToken)
            => Error is not null ? throw Error : Token;
    }

    private static CopilotOutboundTransformer Transformer(
        string baseUrl = "",
        string token = MockToken,
        Exception? tokenError = null)
        => new(new CopilotOutboundParams
        {
            TokenProvider = new MockTokenProvider { Token = token, Error = tokenError },
            BaseUrl = baseUrl,
        });

    private static LlmRequest ChatRequest(string model = "gpt-4o", string text = "Hello, Copilot!")
        => new()
        {
            Model = model,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromText(text),
                },
            ],
        };

    private static HttpResponseData Response(string body, int statusCode = 200)
        => new()
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        };

    private static JsonElement Body(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------ construction

    [Fact]
    public void Constructor_DefaultsTheBaseUrl()
    {
        var transformer = Transformer();

        Assert.Equal(CopilotConstants.DefaultCopilotBaseUrl, transformer.BaseUrl);
        Assert.Equal("openai/chat_completions", transformer.ApiFormat);
    }

    [Fact]
    public void Constructor_KeepsACustomBaseUrl()
    {
        Assert.Equal(
            "https://custom.copilot.api",
            Transformer("https://custom.copilot.api").BaseUrl);
    }

    [Fact]
    public void Constructor_TrimsExactlyOneTrailingSlash()
    {
        // strings.TrimSuffix removes one slash, not all of them.
        Assert.Equal(
            "https://custom.copilot.api",
            Transformer("https://custom.copilot.api/").BaseUrl);
        Assert.Equal(
            "https://custom.copilot.api/",
            Transformer("https://custom.copilot.api//").BaseUrl);
    }

    [Fact]
    public void Constructor_RequiresATokenProvider()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new CopilotOutboundTransformer(new CopilotOutboundParams()));

        Assert.Contains("token provider is required", error.Message);
    }

    // -------------------------------------------------------------- model route

    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5-mini", false)]
    [InlineData("gpt-5.3", true)]
    [InlineData("gpt-5.4", true)]
    [InlineData("gpt-5.4-preview", true)]
    [InlineData("gpt-5.5", true)]
    [InlineData("gpt-5.10", true)]
    [InlineData("gpt-6", true)]
    [InlineData("gpt-6.1", true)]
    [InlineData("gpt-6-preview", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("claude-sonnet-4.6", false)]
    [InlineData("claude-3-5-sonnet", false)]
    [InlineData("grok-4.6", true)]
    [InlineData("grok-4.5", true)]
    [InlineData("Grok-4.6", true)]
    [InlineData("o4-mini", false)]
    [InlineData("gpt-", false)]
    [InlineData("GPT-5.4", true)]
    public void UsesResponsesApi_MatchesTheReferenceTable(string model, bool expected)
        => Assert.Equal(expected, CopilotOutboundTransformer.UsesResponsesApi(model));

    // ----------------------------------------------------------- chat requests

    [Fact]
    public void TransformRequest_BuildsTheChatWireRequest()
    {
        var request = Transformer().TransformRequest(ChatRequest());

        Assert.Equal("POST", request.Method);
        Assert.Equal(
            CopilotConstants.DefaultCopilotBaseUrl + CopilotConstants.CopilotChatCompletionsEndpoint,
            request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));

        // LiteLLM-style editor headers.
        Assert.Equal(
            CopilotConstants.DefaultEditorVersion,
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.EditorVersionHeader));
        Assert.Equal(
            CopilotConstants.DefaultEditorPluginVersion,
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.EditorPluginVersionHeader));
        Assert.Equal(
            CopilotConstants.DefaultUserAgent,
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.UserAgentHeader));
        Assert.Equal(
            CopilotConstants.DefaultCopilotIntegrationId,
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.CopilotIntegrationIdHeader));
        Assert.Equal(
            CopilotConstants.DefaultOpenAiIntent,
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.OpenAiIntentHeader));
        Assert.Equal(
            CopilotConstants.DefaultGitHubApiVersion,
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.GitHubApiVersionHeader));
        Assert.Equal(
            CopilotConstants.DefaultVSCodeUserAgentLib,
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.VSCodeUserAgentLibHeader));

        // Text-only requests do not get the vision header.
        Assert.Empty(HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.CopilotVisionRequestHeader));

        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal(MockToken, request.Auth.ApiKey);
        Assert.Equal("openai/chat_completions", request.ApiFormat);

        var body = Body(request);
        Assert.Equal("gpt-4o", body.GetProperty("model").GetString());
    }

    [Fact]
    public void TransformRequest_RejectsNilRequest()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));

        Assert.Contains("request is nil", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAnEmptyModel()
    {
        var request = ChatRequest();
        request.Model = string.Empty;

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(request));

        Assert.Contains("model is required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsEmptyMessages()
    {
        var request = new LlmRequest { Model = "gpt-4o", Messages = [] };

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(request));

        Assert.Contains("messages are required", error.Message);
    }

    [Fact]
    public void TransformRequest_WrapsATokenFailure()
    {
        var transformer = Transformer(
            tokenError: new InvalidOperationException("token fetch failed"));

        var error = Assert.Throws<WrappedException>(
            () => transformer.TransformRequest(ChatRequest()));

        Assert.Contains("failed to get copilot token: token fetch failed", error.Message);
    }

    [Fact]
    public void TransformRequest_UsesACustomBaseUrl()
    {
        var request = Transformer("https://custom.copilot.github.com")
            .TransformRequest(ChatRequest());

        Assert.Equal(
            "https://custom.copilot.github.com" + CopilotConstants.CopilotChatCompletionsEndpoint,
            request.Url);
    }

    [Theory]
    [InlineData("gpt-5.4")]
    [InlineData("gpt-5.2-codex")]
    [InlineData("grok-4.6")]
    public void TransformRequest_RoutesGpt5AndGrokToTheResponsesApi(string model)
    {
        var request = Transformer().TransformRequest(ChatRequest(model));

        Assert.Equal(CopilotConstants.DefaultCopilotBaseUrl + "/v1/responses", request.Url);
        Assert.Equal("openai/responses", request.ApiFormat);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal(MockToken, request.Auth.ApiKey);

        // The embedded Responses outbound's own headers plus the Copilot set.
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal(
            CopilotConstants.DefaultEditorVersion,
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.EditorVersionHeader));
        Assert.Equal(
            CopilotConstants.DefaultVSCodeUserAgentLib,
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.VSCodeUserAgentLibHeader));
        Assert.Equal(
            "user",
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.InitiatorHeader));
    }

    [Fact]
    public void TransformRequest_ResponsesPathWrapsTransformFailures()
    {
        // A request type the Responses outbound rejects.
        var request = ChatRequest("gpt-5.4");
        request.RequestType = "embedding";

        var error = Assert.Throws<WrappedException>(
            () => Transformer().TransformRequest(request));

        Assert.Contains("failed to transform request for Responses API:", error.Message);
    }

    // ---------------------------------------------------------------- vision

    public static TheoryData<string, LlmRequest, bool> VisionCases()
    {
        var textOnly = ChatRequest();
        var imagePart = ChatRequest();
        imagePart.Messages[0].Content = MessageContent.FromParts(
        [
            new MessageContentPart
            {
                Type = "image_url",
                ImageUrl = new ImageUrl { Url = "https://example.com/image.png" },
            },
            new MessageContentPart { Type = "text", Text = "What's in this image?" },
        ]);
        var dataUrlText = ChatRequest("gpt-4o", "data:image/png;base64,iVBORw0KGgo...");
        var dataUrlPart = ChatRequest();
        dataUrlPart.Messages[0].Content = MessageContent.FromParts(
        [
            new MessageContentPart { Type = "text", Text = "data:image/jpeg;base64,/9j/4AAQ..." },
        ]);
        var mixed = new LlmRequest
        {
            Model = "gpt-4o",
            Messages =
            [
                new Message
                {
                    Role = "system",
                    Content = MessageContent.FromText("You are a helpful assistant"),
                },
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromParts(
                    [
                        new MessageContentPart { Type = "text", Text = "Describe this:" },
                        new MessageContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new ImageUrl
                            {
                                Url = "https://example.com/photo.jpg",
                                Detail = "high",
                            },
                        },
                    ]),
                },
            ],
        };

        return new TheoryData<string, LlmRequest, bool>
        {
            { "text only", textOnly, false },
            { "image_url part", imagePart, true },
            { "data URL in content", dataUrlText, true },
            { "data URL in a text part", dataUrlPart, true },
            { "mixed messages", mixed, true },
        };
    }

    [Theory]
    [MemberData(nameof(VisionCases))]
    public void TransformRequest_SetsTheVisionHeader(string name, LlmRequest request, bool expected)
    {
        _ = name;

        var httpRequest = Transformer().TransformRequest(request);

        var value = HttpClientUtils.HeaderGet(
            httpRequest.Headers,
            CopilotConstants.CopilotVisionRequestHeader);

        if (expected)
        {
            Assert.Equal("true", value);
        }
        else
        {
            Assert.Empty(value);
        }
    }

    // ------------------------------------------------------------- initiator

    [Fact]
    public void TransformRequest_DefaultsInitiatorFromTheLastMessage()
    {
        var request = Transformer().TransformRequest(ChatRequest());

        Assert.Equal(
            "user",
            HttpClientUtils.HeaderGet(request.Headers, CopilotConstants.InitiatorHeader));
    }

    [Theory]
    [InlineData("user", "user")]
    [InlineData("agent", "agent")]
    [InlineData("USER", "user")]
    [InlineData("Agent", "agent")]
    [InlineData("editor", "user")]
    [InlineData("", "user")]
    public void TransformRequest_ForwardsAValidInitiatorHeader(string value, string expected)
    {
        var request = ChatRequest();
        request.RawRequest = new HttpRequestData
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
        };
        if (value.Length > 0)
        {
            HttpClientUtils.HeaderSet(
                request.RawRequest.Headers,
                CopilotConstants.InitiatorHeader,
                value);
        }

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal(
            expected,
            HttpClientUtils.HeaderGet(httpRequest.Headers, CopilotConstants.InitiatorHeader));
    }

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", true)]
    [InlineData("data:image/jpeg;base64,/9j/4AAQ=", true)]
    [InlineData("data:image/webp;base64,UklGR=", true)]
    [InlineData("data:image/gif;base64,R0lGOD=", true)]
    [InlineData("data:image/svg+xml;base64,PHN2Zw=", true)]
    [InlineData("Hello, world!", false)]
    [InlineData("https://example.com/image.png", false)]
    [InlineData("data:text/plain;base64,SGVsbG8=", false)]
    [InlineData("data:application/pdf;base64,JVBERi0=", false)]
    [InlineData("", false)]
    [InlineData("data:ima", false)]
    [InlineData("DATA:IMAGE/PNG;base64,ABC=", false)]
    public void IsImageDataUrl_MatchesTheReferenceTable(string content, bool expected)
        => Assert.Equal(expected, CopilotOutboundTransformer.IsImageDataUrl(content));

    public static TheoryData<string, LlmRequest, bool> VisionContentCases()
    {
        static LlmRequest WithPart(MessageContentPart part)
            => new()
            {
                Model = "gpt-4o",
                Messages =
                [
                    new Message
                    {
                        Role = "user",
                        Content = MessageContent.FromParts([part]),
                    },
                ],
            };

        return new TheoryData<string, LlmRequest, bool>
        {
            { "text only", ChatRequest("gpt-4o", "Just text"), false },
            {
                "image_url part",
                WithPart(new MessageContentPart
                {
                    Type = "image_url",
                    ImageUrl = new ImageUrl { Url = "https://example.com/image.png" },
                }),
                true
            },
            {
                "image_url without an image object",
                WithPart(new MessageContentPart { Type = "image_url" }),
                true
            },
            { "png data URL", ChatRequest("gpt-4o", "data:image/png;base64,abc123"), true },
            { "jpeg data URL", ChatRequest("gpt-4o", "data:image/jpeg;base64,xyz789"), true },
            { "webp data URL", ChatRequest("gpt-4o", "data:image/webp;base64,webp123"), true },
            {
                "data URL in a text part",
                WithPart(new MessageContentPart { Type = "text", Text = "data:image/gif;base64,gif456" }),
                true
            },
            { "non-image data URL", ChatRequest("gpt-4o", "data:text/plain;base64,hello"), false },
            {
                "regular image URL",
                ChatRequest("gpt-4o", "https://example.com/images/photo.png"),
                false
            },
            { "empty content", ChatRequest("gpt-4o", ""), false },
            {
                "absent content",
                new LlmRequest
                {
                    Model = "gpt-4o",
                    Messages = [new Message { Role = "user", Content = new MessageContent() }],
                },
                false
            },
            {
                "text part",
                WithPart(new MessageContentPart { Type = "text", Text = "Just text content" }),
                false
            },
            {
                "mixed messages",
                new LlmRequest
                {
                    Model = "gpt-4o",
                    Messages =
                    [
                        new Message
                        {
                            Role = "system",
                            Content = MessageContent.FromText("You are helpful"),
                        },
                        new Message
                        {
                            Role = "user",
                            Content = MessageContent.FromParts(
                            [
                                new MessageContentPart { Type = "text", Text = "Look at this:" },
                                new MessageContentPart
                                {
                                    Type = "image_url",
                                    ImageUrl = new ImageUrl { Url = "https://example.com/img.png" },
                                },
                            ]),
                        },
                        new Message
                        {
                            Role = "assistant",
                            Content = MessageContent.FromText("I see the image"),
                        },
                    ],
                },
                true
            },
            {
                "data URL in a later message",
                new LlmRequest
                {
                    Model = "gpt-4o",
                    Messages =
                    [
                        new Message { Role = "user", Content = MessageContent.FromText("First message") },
                        new Message
                        {
                            Role = "user",
                            Content = MessageContent.FromText("data:image/png;base64,second"),
                        },
                    ],
                },
                true
            },
            {
                "assistant vision message",
                new LlmRequest
                {
                    Model = "gpt-4o",
                    Messages =
                    [
                        new Message
                        {
                            Role = "assistant",
                            Content = MessageContent.FromText("data:image/png;base64,assistant"),
                        },
                    ],
                },
                true
            },
        };
    }

    [Theory]
    [MemberData(nameof(VisionContentCases))]
    public void HasVisionContent_MatchesTheReferenceTable(
        string name,
        LlmRequest request,
        bool expected)
    {
        _ = name;

        Assert.Equal(expected, CopilotOutboundTransformer.HasVisionContent(request));
    }

    // -------------------------------------------------------- initiator tables

    public static TheoryData<string, List<Message>, string> InitiatorCases()
    {
        static Message Message(string role, string? attribution = null, MessageContent? content = null)
            => new()
            {
                Role = role,
                Attribution = attribution,
                Content = content ?? MessageContent.FromText("text"),
            };

        var toolResultLast = MessageContent.FromParts(
        [
            new MessageContentPart { Type = "text", Text = "What about" },
            new MessageContentPart { Type = "tool_result", Text = "result" },
        ]);
        var toolResultFirst = MessageContent.FromParts(
        [
            new MessageContentPart { Type = "tool_result", Text = "result" },
            new MessageContentPart { Type = "text", Text = "Now answer my question" },
        ]);

        return new TheoryData<string, List<Message>, string>
        {
            { "empty messages", [], "user" },
            { "last message from user", [Message("user")], "user" },
            {
                "last message from assistant",
                [Message("user"), Message("assistant")],
                "agent"
            },
            {
                "tool_result in the last position",
                [Message("user"), Message("assistant"), Message("user", content: toolResultLast)],
                "agent"
            },
            {
                "last message from system",
                [Message("system"), Message("user"), Message("system")],
                "agent"
            },
            {
                "tool_result not in the last position",
                [Message("user"), Message("assistant"), Message("user", content: toolResultFirst)],
                "user"
            },
            { "attribution user", [Message("user", "user")], "user" },
            { "attribution agent", [Message("user", "agent")], "agent" },
            { "attribution overrides the role", [Message("assistant", "user")], "user" },
            { "attribution is normalized", [Message("user", "  AGENT  ")], "agent" },
            { "invalid attribution falls through", [Message("user", "invalid")], "user" },
            {
                "invalid attribution with an agent role",
                [Message("assistant", "invalid")],
                "agent"
            },
        };
    }

    [Theory]
    [MemberData(nameof(InitiatorCases))]
    public void InferCopilotInitiator_MatchesTheReferenceTable(
        string name,
        List<Message> messages,
        string expected)
    {
        _ = name;

        Assert.Equal(expected, CopilotOutboundTransformer.InferCopilotInitiator(messages));
    }

    // ------------------------------------------------------------- responses

    [Fact]
    public void TransformResponse_ReturnsTheOpenAiChatResponse()
    {
        const string body = """
            {
              "id": "chatcmpl-123",
              "object": "chat.completion",
              "created": 1700000000,
              "model": "gpt-4o",
              "choices": [
                {
                  "index": 0,
                  "message": { "role": "assistant", "content": "Hello! How can I help you today?" },
                  "finish_reason": "stop"
                }
              ]
            }
            """;

        var response = Transformer().TransformResponse(Response(body));

        Assert.Equal("chatcmpl-123", response.Id);
        Assert.Equal("chat.completion", response.Object);
        Assert.Equal("gpt-4o", response.Model);
        var choice = Assert.Single(response.Choices);
        Assert.Equal("assistant", choice.Message!.Role);
        Assert.Equal("Hello! How can I help you today?", choice.Message.Content!.Content);
        Assert.Equal("stop", choice.FinishReason);
    }

    [Fact]
    public void TransformResponse_DelegatesResponsesObjects()
    {
        const string body = """
            {
              "id": "resp_1",
              "object": "response",
              "created_at": 1700000000,
              "model": "gpt-5.4",
              "status": "completed",
              "output": [
                {
                  "type": "message",
                  "id": "msg_1",
                  "role": "assistant",
                  "content": [{ "type": "output_text", "text": "hi" }]
                }
              ]
            }
            """;

        var response = Transformer().TransformResponse(Response(body));

        Assert.Equal("resp_1", response.Id);
    }

    [Fact]
    public void TransformResponse_UnwrapsAResponseEnvelope()
    {
        const string body = """
            {
              "response": {
                "id": "resp_wrapped",
                "object": "response",
                "created_at": 1700000000,
                "model": "gpt-5.4",
                "status": "completed",
                "output": []
              }
            }
            """;

        var response = Transformer().TransformResponse(Response(body));

        Assert.Equal("resp_wrapped", response.Id);
    }

    [Fact]
    public void TransformResponse_RejectsANilResponse()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(null));

        Assert.Contains("http response is nil", error.Message);
    }

    [Theory]
    [InlineData(400, "bad request", "HTTP error 400: (body: bad request, length: 11)")]
    [InlineData(500, "", "HTTP error 500: (empty body)")]
    public void TransformResponse_ReportsHttpErrors(int statusCode, string body, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(Response(body, statusCode)));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void TransformResponse_RejectsAnEmptyBody()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(Response(string.Empty)));

        Assert.Contains("response body is empty", error.Message);
    }

    [Fact]
    public void TransformResponse_RejectsInvalidJson()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(Response("not valid json")));

        Assert.Contains("failed to unmarshal response", error.Message);
    }

    [Fact]
    public void TransformResponse_ReportsTheLongBodyShape()
    {
        var body = new string('a', 120);

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(Response(body, 400)));

        Assert.Equal(
            "HTTP error 400: (first 100 chars: "
            + new string('a', 100)
            + ", total length: 120)",
            error.Message);
    }

    // ----------------------------------------------------------------- errors

    [Fact]
    public void TransformError_NullErrorUsesTheGenericEnvelope()
    {
        var error = Transformer().TransformError(null);

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Internal Server Error", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void TransformError_PrefersTheErrorField()
    {
        var error = Transformer().TransformError(new HttpClientError(
            "POST",
            "https://api.githubcopilot.com/chat/completions",
            401,
            "401 Unauthorized",
            """{"error": {"message": "Invalid API key", "type": "authentication_error"}}"""u8.ToArray()));

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("Invalid API key", error.Detail.Message);
        Assert.Equal("authentication_error", error.Detail.Type);
    }

    [Fact]
    public void TransformError_UsesTheErrorsField()
    {
        var error = Transformer().TransformError(new HttpClientError(
            "POST",
            "https://api.githubcopilot.com/chat/completions",
            429,
            "429 Too Many Requests",
            """{"errors": {"message": "Rate limit exceeded", "type": "rate_limit_error"}}"""u8.ToArray()));

        Assert.Equal(429, error.StatusCode);
        Assert.Equal("Rate limit exceeded", error.Detail.Message);
        Assert.Equal("rate_limit_error", error.Detail.Type);
    }

    [Theory]
    [InlineData(503, "service unavailable", "Service Unavailable")]
    [InlineData(502, "", "Bad Gateway")]
    public void TransformError_FallsBackToTheStatusText(
        int statusCode,
        string body,
        string expectedMessage)
    {
        var error = Transformer().TransformError(new HttpClientError(
            "POST",
            "https://api.githubcopilot.com/chat/completions",
            statusCode,
            statusCode + " error",
            Encoding.UTF8.GetBytes(body)));

        Assert.Equal(statusCode, error.StatusCode);
        Assert.Equal(expectedMessage, error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    // ---------------------------------------------------------- stream repair

    [Fact]
    public void ConvertCopilotStreamEvent_RewritesOutputItemAdded()
    {
        var state = new CopilotStreamConversionState();

        var result = CopilotOutboundTransformer.ConvertCopilotStreamEvent(
            """{"type":"response.output_item.added","item":{"id":"fc_abc","call_id":"call_0","type":"function_call","arguments":""}}"""u8.ToArray(),
            state);

        Assert.Equal(
            """{"item":{"arguments":"","call_id":"call_0","id":"call_0","name":"function","type":"function_call"},"type":"response.output_item.added"}""",
            Encoding.UTF8.GetString(result));
        Assert.Equal("call_0", state.MostRecentCallId);
        Assert.Equal("call_0", state.ItemIdToCallId["call_0"]);
        Assert.Equal("call_0", state.ItemIdToCallId["fc_abc"]);
    }

    [Fact]
    public void ConvertCopilotStreamEvent_KeepsAnExistingItemName()
    {
        var result = CopilotOutboundTransformer.ConvertCopilotStreamEvent(
            """{"type":"response.output_item.added","item":{"id":"fc_abc","call_id":"call_0","name":"custom","type":"function_call"}}"""u8.ToArray(),
            new CopilotStreamConversionState());

        Assert.Equal(
            """{"item":{"call_id":"call_0","id":"call_0","name":"custom","type":"function_call"},"type":"response.output_item.added"}""",
            Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void ConvertCopilotStreamEvent_RequiresACallIdToRewrite()
    {
        var state = new CopilotStreamConversionState();
        var data = """{"type":"response.output_item.added","item":{"id":"fc_abc"}}"""u8.ToArray();

        var result = CopilotOutboundTransformer.ConvertCopilotStreamEvent(data, state);

        Assert.Same(data, result);
        Assert.Null(state.MostRecentCallId);
    }

    [Fact]
    public void ConvertCopilotStreamEvent_RemapsDeltaItemIds()
    {
        var state = new CopilotStreamConversionState();
        state.ItemIdToCallId["fc_abc"] = "call_0";

        var result = CopilotOutboundTransformer.ConvertCopilotStreamEvent(
            """{"type":"response.function_call_arguments.delta","item_id":"fc_abc","delta":"{\"a\""}"""u8.ToArray(),
            state);

        Assert.Equal(
            """{"delta":"{\"a\"","item_id":"call_0","type":"response.function_call_arguments.delta"}""",
            Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void ConvertCopilotStreamEvent_FallsBackToTheMostRecentCallId()
    {
        var state = new CopilotStreamConversionState { MostRecentCallId = "call_0" };

        var result = CopilotOutboundTransformer.ConvertCopilotStreamEvent(
            """{"type":"response.function_call_arguments.delta","item_id":"unknown","delta":"x"}"""u8.ToArray(),
            state);

        Assert.Equal("call_0", JsonDocument.Parse(result).RootElement.GetProperty("item_id").GetString());
    }

    [Fact]
    public void ConvertCopilotStreamEvent_AddsACallIdToTheDoneEvent()
    {
        var state = new CopilotStreamConversionState();
        state.ItemIdToCallId["fc_abc"] = "call_0";

        var result = CopilotOutboundTransformer.ConvertCopilotStreamEvent(
            """{"type":"response.function_call_arguments.done","item_id":"fc_abc","arguments":"{}"}"""u8.ToArray(),
            state);

        Assert.Equal(
            """{"arguments":"{}","call_id":"call_0","item_id":"call_0","type":"response.function_call_arguments.done"}""",
            Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void ConvertCopilotStreamEvent_RewritesOutputItemDone()
    {
        var result = CopilotOutboundTransformer.ConvertCopilotStreamEvent(
            """{"type":"response.output_item.done","item":{"id":"hash","call_id":"call_0","type":"function_call","arguments":"{}"}}"""u8.ToArray(),
            new CopilotStreamConversionState());

        Assert.Equal(
            """{"item":{"arguments":"{}","call_id":"call_0","id":"call_0","type":"function_call"},"type":"response.output_item.done"}""",
            Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void ConvertCopilotStreamEvent_LeavesUnknownEventsUntouched()
    {
        var data = """{"type":"response.created","response":{"id":"resp_1"}}"""u8.ToArray();

        var result = CopilotOutboundTransformer.ConvertCopilotStreamEvent(
            data,
            new CopilotStreamConversionState());

        Assert.Same(data, result);
    }

    [Fact]
    public void ConvertCopilotStreamEvent_LeavesMalformedEventsUntouched()
    {
        var data = "{not-json"u8.ToArray();

        var result = CopilotOutboundTransformer.ConvertCopilotStreamEvent(
            data,
            new CopilotStreamConversionState());

        Assert.Same(data, result);
    }

    [Fact]
    public void IsResponsesApiStream_DetectsTheResponsesEventTypes()
    {
        Assert.True(CopilotOutboundTransformer.IsResponsesApiStream(
            [new StreamEvent { Data = """{"type":"response.created"}"""u8.ToArray() }]));
        Assert.True(CopilotOutboundTransformer.IsResponsesApiStream(
            [new StreamEvent { Data = """{"type":"response.completed"}"""u8.ToArray() }]));
        Assert.True(CopilotOutboundTransformer.IsResponsesApiStream(
            [new StreamEvent { Data = """{"type":"response.in_progress"}"""u8.ToArray() }]));
        Assert.True(CopilotOutboundTransformer.IsResponsesApiStream(
            [new StreamEvent { Data = """{"type":"response.output_item.added"}"""u8.ToArray() }]));

        Assert.False(CopilotOutboundTransformer.IsResponsesApiStream(
            [new StreamEvent { Data = """{"type":"chat.completion.chunk"}"""u8.ToArray() }]));

        // bytes.Contains: the reference looks for the exact no-space literal.
        Assert.False(CopilotOutboundTransformer.IsResponsesApiStream(
            [new StreamEvent { Data = """{"type": "response.created"}"""u8.ToArray() }]));

        // Empty chunks are skipped, not fatal.
        Assert.False(CopilotOutboundTransformer.IsResponsesApiStream(
            [new StreamEvent { Data = [] }]));
    }

    [Fact]
    public void AggregateStreamChunks_UsesTheOpenAiAggregatorForChatChunks()
    {
        var chunks = new List<StreamEvent>
        {
            new()
            {
                Data = """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"gpt-4o","choices":[{"index":0,"delta":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}]}"""u8.ToArray(),
            },
        };

        var body = Transformer().AggregateStreamChunks(null, chunks, out _);
        using var document = JsonDocument.Parse(body);

        Assert.Equal("chatcmpl-1", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("chat.completion", document.RootElement.GetProperty("object").GetString());
    }

    // -------------------------------------------------------------- stream I/O

    [Fact]
    public void TransformStream_DelegatesChatStreamsToTheOpenAiTransformer()
    {
        var stream = new ListStream<StreamEvent>(
        [
            new StreamEvent
            {
                Type = "chat.completion.chunk",
                Data = """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"gpt-4o","choices":[{"index":0,"delta":{"role":"assistant","content":"Hi"}}]}"""u8.ToArray(),
            },
            new StreamEvent
            {
                Type = "chat.completion.chunk",
                Data = """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"gpt-4o","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""u8.ToArray(),
            },
        ]);

        using var responses = Transformer().TransformStream(null, stream);

        var items = new List<LlmResponse>();
        while (responses.Next())
        {
            if (responses.Current is { } item)
            {
                items.Add(item);
            }
        }

        Assert.NotEmpty(items);
        Assert.Equal("assistant", items[0].Choices[0].Delta!.Role);
        Assert.Equal("Hi", items[0].Choices[0].Delta!.Content!.Content);
    }

    private sealed class ListStream<T>(IReadOnlyList<T> items) : IStream<T>
        where T : class
    {
        private int _index;

        public T? Current { get; private set; }

        public Exception? Err => null;

        public bool Next()
        {
            if (_index >= items.Count)
            {
                return false;
            }

            Current = items[_index];
            _index++;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
