using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Xai;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the xAI outbound (llm/transformer/xai and its
/// outbound_test.go): OpenAI wire format with reasoning_content, the default
/// base URL, Grok-specific field stripping, and the empty-delta stream filter.
/// </summary>
public sealed class XaiOutboundTransformerTests
{
    // Reference TestOutboundTransformer_TransformStream_FilterEmptyEvents
    // (the reference test applies the same predicate through a test helper).
    [Fact]
    public void IsValidResponse_FiltersEmptyDelta()
        => Assert.False(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice { Index = 0, Delta = new Message() })));

    [Fact]
    public void IsValidResponse_KeepsTextContent()
        => Assert.True(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice
            {
                Index = 0,
                Delta = new Message { Content = MessageContent.FromText("Hello") },
            })));

    [Fact]
    public void IsValidResponse_FiltersEmptyTextContent()
        => Assert.False(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice
            {
                Index = 0,
                Delta = new Message { Content = MessageContent.FromText(string.Empty) },
            })));

    [Fact]
    public void IsValidResponse_KeepsRole()
        => Assert.True(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice { Index = 0, Delta = new Message { Role = "assistant" } })));

    [Fact]
    public void IsValidResponse_KeepsFinishReason()
        => Assert.True(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice
            {
                Index = 0,
                Delta = new Message(),
                FinishReason = "stop",
            })));

    [Fact]
    public void IsValidResponse_KeepsToolCalls()
        => Assert.True(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice
            {
                Index = 0,
                Delta = new Message
                {
                    ToolCalls = [new ToolCall
                    {
                        Id = "call_123",
                        Type = "function",
                        Function = new FunctionCall { Name = "test_function", Arguments = "{}" },
                    }],
                },
            })));

    [Fact]
    public void IsValidResponse_KeepsRefusal()
        => Assert.True(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice
            {
                Index = 0,
                Delta = new Message { Refusal = "I cannot help with that" },
            })));

    [Fact]
    public void IsValidResponse_KeepsReasoningContent()
        => Assert.True(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice
            {
                Index = 0,
                Delta = new Message { ReasoningContent = "Let me think about this..." },
            })));

    [Fact]
    public void IsValidResponse_KeepsMultipleContentParts()
        => Assert.True(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice
            {
                Index = 0,
                Delta = new Message
                {
                    Content = MessageContent.FromParts(
                        [new MessageContentPart { Type = "text", Text = "Hello" }]),
                },
            })));

    [Fact]
    public void IsValidResponse_AlwaysKeepsDoneResponse()
        => Assert.True(XaiOutboundTransformer.IsValidResponse(LlmResponse.DoneResponse));

    [Fact]
    public void IsValidResponse_FiltersNoChoices()
        => Assert.False(XaiOutboundTransformer.IsValidResponse(
            new LlmResponse { Object = "chat.completion.chunk" }));

    [Fact]
    public void IsValidResponse_FiltersNilDelta()
        => Assert.False(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice { Index = 0, Delta = null })));

    [Fact]
    public void IsValidResponse_FiltersMixedEventsIndependently()
    {
        Assert.False(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice { Index = 0, Delta = new Message() }, "test-11a")));
        Assert.True(XaiOutboundTransformer.IsValidResponse(
            Chunk(
                new Choice
                {
                    Index = 0,
                    Delta = new Message { Content = MessageContent.FromText("Hello") },
                },
                "test-11b")));
        Assert.False(XaiOutboundTransformer.IsValidResponse(
            Chunk(new Choice { Index = 0, Delta = new Message() }, "test-11c")));
    }

    // Reference TestOutboundTransformer_TransformStream_RealXAIEmptyEvent.
    [Fact]
    public void TransformStream_FiltersRealXaiEmptyEvent()
    {
        var emptyEvent = Encoding.UTF8.GetBytes(
            """
            {
              "id": "c3f2e709-9d83-5dba-aff3-e0a2e5dcefdf_us-east-1",
              "object": "chat.completion.chunk",
              "created": 1758555599,
              "model": "grok-code-fast-1",
              "choices": [{"index": 0, "delta": {}}],
              "system_fingerprint": "fp_10f00c862d"
            }
            """);

        using var filtered = Transformer().TransformStream(
            null,
            new SliceStream<StreamEvent>([new StreamEvent { Data = emptyEvent }]));

        var responses = GoStreams.All(filtered, out var error);

        Assert.Null(error);
        Assert.Empty(responses);
    }

    [Fact]
    public void TransformStream_KeepsContentAndDoneEvents()
    {
        var events = new List<StreamEvent>
        {
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"id":"x","object":"chat.completion.chunk","created":123,"model":"grok-code-fast-1","choices":[{"index":0,"delta":{}}]}"""),
            },
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"id":"x","object":"chat.completion.chunk","created":123,"model":"grok-code-fast-1","choices":[{"index":0,"delta":{"content":"Hello"}}]}"""),
            },
            new() { Data = "[DONE]"u8.ToArray() },
        };

        using var filtered = Transformer().TransformStream(
            null,
            new SliceStream<StreamEvent>(events));

        var responses = GoStreams.All(filtered, out var error);

        Assert.Null(error);
        Assert.Equal(2, responses.Count);
        Assert.Equal(
            "Hello",
            Assert.Single(responses[0].Choices).Delta!.Content!.Content);
        Assert.Same(LlmResponse.DoneResponse, responses[1]);
    }

    [Theory]
    [InlineData("grok-4", true)]
    [InlineData("grok-3", false)]
    [InlineData("grok-3-mini", false)]
    public void TransformRequest_StripsModelSpecificFields(string model, bool stripsReasoningEffort)
    {
        var request = Request(model);
        request.ReasoningEffort = "high";
        request.PresencePenalty = 0.5;
        request.FrequencyPenalty = 0.25;
        request.Stop = new Stop { Single = "STOP" };

        var httpRequest = Transformer().TransformRequest(request);

        // The reference mutates the caller's unified request before delegating.
        Assert.Equal(stripsReasoningEffort ? null : "high", request.ReasoningEffort);
        Assert.Null(request.PresencePenalty);
        Assert.Null(request.FrequencyPenalty);
        Assert.Null(request.Stop);

        var body = Body(httpRequest);
        Assert.False(body.TryGetProperty("presence_penalty", out _));
        Assert.False(body.TryGetProperty("frequency_penalty", out _));
        Assert.False(body.TryGetProperty("stop", out _));

        if (stripsReasoningEffort)
        {
            Assert.False(body.TryGetProperty("reasoning_effort", out _));
        }
        else
        {
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
        }
    }

    [Theory]
    [InlineData("grok-code-fast-1")]
    [InlineData("grok-2")]
    public void TransformRequest_KeepsFieldsForOtherModels(string model)
    {
        var request = Request(model);
        request.ReasoningEffort = "high";
        request.PresencePenalty = 0.5;
        request.FrequencyPenalty = 0.25;
        request.Stop = new Stop { Single = "STOP" };

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0.5, body.GetProperty("presence_penalty").GetDouble());
        Assert.Equal(0.25, body.GetProperty("frequency_penalty").GetDouble());
        Assert.Equal("STOP", body.GetProperty("stop").GetString());
    }

    [Fact]
    public void TransformRequest_UsesDefaultBaseUrlAndMutatesConfig()
    {
        var config = Config(string.Empty);
        var transformer = new XaiOutboundTransformer(config);

        Assert.Equal(XaiOutboundTransformer.DefaultBaseUrl, config.BaseUrl);

        var httpRequest = transformer.TransformRequest(Request("grok-3"));
        Assert.Equal("https://api.x.ai/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("xai-key", httpRequest.Auth?.ApiKey);
    }

    [Fact]
    public void TransformRequest_UsesCustomBaseUrl()
    {
        var httpRequest = Transformer("https://api.x.ai.evil/v1")
            .TransformRequest(Request("grok-3"));

        Assert.Equal("https://api.x.ai.evil/v1/chat/completions", httpRequest.Url);
    }

    [Fact]
    public void TransformRequest_NilRequest()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));

        Assert.Equal("chat completion request is nil", error.Message);
    }

    [Fact]
    public void TransformRequest_RequiresModel()
    {
        var request = Request("grok-3");
        request.Model = string.Empty;

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("model is required", error.Message);
    }

    [Fact]
    public void TransformRequest_RequiresMessages()
    {
        var request = Request("grok-3");
        request.Messages = [];

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Equal("invalid request: messages are required", error.Message);
    }

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new XaiOutboundTransformer(null!));
        Assert.Equal(
            "invalid xAI transformer configuration: config cannot be nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new XaiOutboundTransformer(new XaiOutboundConfig
            {
                BaseUrl = "https://api.x.ai/v1",
            }));
        Assert.Equal(
            "invalid xAI transformer configuration: API key provider is required",
            missingKey.Message);
    }

    [Fact]
    public void SelectedApiKeyTracksTheLatestRequest()
    {
        var transformer = Transformer();
        Assert.Null(transformer.SelectedApiKey);

        transformer.TransformRequest(Request("grok-3"));

        Assert.Equal("xai-key", transformer.SelectedApiKey);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static XaiOutboundConfig Config(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        ApiKeyProvider = new StaticApiKeyProvider("xai-key"),
    };

    private static XaiOutboundTransformer Transformer(string baseUrl = "https://api.x.ai/v1")
        => new(Config(baseUrl));

    private static LlmRequest Request(string model) => new()
    {
        Model = model,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
    };

    private static LlmResponse Chunk(Choice choice, string id = "test")
        => new()
        {
            Id = id,
            Object = "chat.completion.chunk",
            Created = 1234567890,
            Model = "grok-code-fast-1",
            Choices = [choice],
        };

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
