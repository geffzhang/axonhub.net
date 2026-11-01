using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Nanogpt;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the NanoGPT outbound (llm/transformer/nanogpt and its
/// outbound_test.go / model_test.go): the OpenAI chat wire format with
/// top-level reasoning, XML tool-call extraction and the status diagnostics.
/// </summary>
public sealed class NanogptOutboundTransformerTests
{
    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new NanogptOutboundTransformer(null!));
        Assert.Equal(
            "invalid NanoGPT transformer configuration: config cannot be nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new NanogptOutboundTransformer(new NanogptOutboundConfig
            {
                BaseUrl = "https://nano-gpt.com/api/v1",
            }));
        // The inner OpenAI validator throws ArgumentException; the port keeps
        // its parameter suffix, so only the Go-compatible prefix is pinned (C101).
        Assert.StartsWith(
            "invalid NanoGPT transformer configuration: "
            + "invalid OpenAI transformer configuration: ",
            missingKey.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "API key provider is required",
            missingKey.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRequest_DelegatesToTheOpenAiOutbound()
    {
        var transformer = Transformer();
        var httpRequest = transformer.TransformRequest(SimpleRequest());

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);
        Assert.Equal("https://nano-gpt.com/api/v1/chat/completions", httpRequest.Url);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, httpRequest.ApiFormat);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("test-api-key", httpRequest.Auth!.ApiKey);
        Assert.Equal("test-api-key", transformer.SelectedApiKey);
    }

    // Reference TestOutboundTransformer_TransformResponse "valid response with
    // reasoning field" and "without reasoning field".
    [Fact]
    public void TransformResponse_MapsReasoningToReasoningContent()
    {
        var result = Transformer().TransformResponse(Response(
            """
            {
              "id": "test-123",
              "object": "chat.completion",
              "created": 1234567890,
              "model": "zai-org/glm-4.7:thinking",
              "choices": [{
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": "Hello!",
                  "reasoning": "Thinking process..."
                },
                "finish_reason": "stop"
              }]
            }
            """));

        Assert.Equal("test-123", result.Id);
        var message = Assert.Single(result.Choices).Message!;
        Assert.Equal("Hello!", message.Content!.Content);
        Assert.Equal("Thinking process...", message.ReasoningContent);

        var withoutReasoning = Transformer().TransformResponse(Response(
            """
            {"choices":[{"index":0,"message":{"role":"assistant","content":"Hi there!"},"finish_reason":"stop"}]}
            """));

        var plain = Assert.Single(withoutReasoning.Choices).Message!;
        Assert.Equal("Hi there!", plain.Content!.Content);
        Assert.Null(plain.ReasoningContent);
    }

    [Fact]
    public void TransformResponse_ExtractsXmlToolCallsFromContent()
    {
        var result = Transformer().TransformResponse(Response(
            """
            {"object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"<Write file_path=\"/test.txt\">hello</Write>"},"finish_reason":"tool_calls"}]}
            """));

        var message = Assert.Single(result.Choices).Message!;
        var toolCall = Assert.Single(message.ToolCalls!);
        Assert.Equal("write", toolCall.Function.Name);
        Assert.Equal(
            """{"content":"hello","file_path":"/test.txt"}""",
            toolCall.Function.Arguments);
        Assert.StartsWith("nanogpt_", toolCall.Id, StringComparison.Ordinal);
        // All content was consumed by the tool call: the zero MessageContent
        // has neither a string nor a part list.
        Assert.Null(message.Content!.Content);
        Assert.Null(message.Content.MultipleContent);
    }

    [Fact]
    public void TransformResponse_KeepsRemainingContentAfterXmlToolCalls()
    {
        var result = Transformer().TransformResponse(Response(
            """
            {"object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"<Write file_path=\"/test.txt\">hello</Write>\nDone"}}]}
            """));

        var message = Assert.Single(result.Choices).Message!;
        Assert.Single(message.ToolCalls!);
        Assert.Equal("Done", message.Content!.Content);
    }

    // Reference "embedding request routes to embedded OpenAI transformer".
    [Fact]
    public void TransformResponse_EmbeddingDelegatesToTheOpenAiOutbound()
    {
        var response = Response(
            """
            {
              "object": "list",
              "data": [{"object": "embedding", "embedding": [0.1, 0.2, 0.3], "index": 0}],
              "model": "text-embedding-3-small",
              "usage": {"prompt_tokens": 10, "total_tokens": 10}
            }
            """);
        response.Request!.ApiFormat = ApiFormats.OpenAIEmbedding;

        var result = Transformer().TransformResponse(response);

        Assert.NotNull(result.Embedding);
        Assert.Equal(3, Assert.Single(result.Embedding!.Data!).Embedding.Values!.Count);
    }

    // Reference "image generation request routes to embedded OpenAI transformer".
    [Fact]
    public void TransformResponse_ImageGenerationDelegatesToTheOpenAiOutbound()
    {
        var response = Response(
            """{"created":1234567890,"data":[{"b64_json":"iVBORw0KGgo="}]}""");
        response.Request!.ApiFormat = ApiFormats.OpenAIImageGeneration;

        var result = Transformer().TransformResponse(response);

        Assert.NotNull(result.Image);
        Assert.Equal("iVBORw0KGgo=", Assert.Single(result.Image!.Data).B64JSON);
    }

    // Reference "nil request in httpResp uses NanoGPT parsing".
    [Fact]
    public void TransformResponse_WithoutARequestUsesNanoGptParsing()
    {
        var response = Response(
            """{"id":"chat-456","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Response without request info"},"finish_reason":"stop"}]}""");
        response.Request = null;

        var message = Assert.Single(Transformer().TransformResponse(response).Choices).Message!;
        Assert.Equal("Response without request info", message.Content!.Content);
    }

    [Fact]
    public void TransformResponse_Errors()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(null));
        Assert.Equal("http response is nil", nil.Message);

        Assert.Equal(
            "response body is empty",
            Assert.Throws<InvalidOperationException>(
                () => Transformer().TransformResponse(new HttpResponseData { StatusCode = 200 })).Message);

        Assert.StartsWith(
            "failed to unmarshal chat completion response: ",
            Assert.Throws<InvalidOperationException>(
                () => Transformer().TransformResponse(Response("not json"))).Message,
            StringComparison.Ordinal);
    }

    // The status diagnostics prefer {"error": string}, then the raw body.
    [Fact]
    public void TransformResponse_StatusErrorsPreferTheStringErrorField()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 400,
                Body = """{"error":"quota exceeded"}"""u8.ToArray(),
            }));
        Assert.Equal("HTTP error 400: quota exceeded", error.Message);

        var raw = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 502,
                Body = """{"error":{"message":"bad"}}"""u8.ToArray(),
            }));
        Assert.Equal("""HTTP error 502: {"error":{"message":"bad"}}""", raw.Message);

        var empty = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 503,
            }));
        Assert.Equal("HTTP error 503", empty.Message);
    }

    // Reference TestOutboundTransformer_AggregateStreamChunks.
    [Fact]
    public void AggregateStreamChunks_MergesContent()
    {
        var chunks = new List<StreamEvent>
        {
            new() { Data = """{"id":"test","choices":[{"index":0,"delta":{"content":"Hello"}}]}"""u8.ToArray() },
            new() { Data = """{"id":"test","choices":[{"index":0,"delta":{"content":" World"}}]}"""u8.ToArray() },
        };

        var data = Transformer().AggregateStreamChunks(null, chunks, out var meta);

        var body = Encoding.UTF8.GetString(data);
        Assert.Contains("Hello", body, StringComparison.Ordinal);
        Assert.Contains("World", body, StringComparison.Ordinal);
        Assert.Equal("test", meta.Id);
    }

    [Fact]
    public void TransformStream_FiltersDoneAndAppendsItsOwn()
    {
        using var stream = Transformer().TransformStream(
            null,
            new SliceStream<StreamEvent>(
            [
                new StreamEvent
                {
                    Data = """{"id":"x","choices":[{"index":0,"delta":{"content":"Hi"}}]}"""u8.ToArray(),
                },
                new StreamEvent { Data = "[DONE]"u8.ToArray() },
            ]));

        var responses = GoStreams.All(stream, out var error);

        Assert.Null(error);
        Assert.Equal(2, responses.Count);
        Assert.Equal("Hi", Assert.Single(responses[0].Choices).Delta!.Content!.Content);
        Assert.Same(LlmResponse.DoneResponse, responses[1]);
    }

    [Fact]
    public void TransformStream_SurfacesInStreamErrors()
    {
        using var stream = Transformer().TransformStream(
            null,
            new SliceStream<StreamEvent>(
                [new StreamEvent { Data = """{"error":"boom"}"""u8.ToArray() }]));

        GoStreams.All(stream, out var error);

        var responseError = Assert.IsType<ResponseError>(error);
        Assert.Equal("boom", responseError.Detail.Message);
    }

    // Reference TestResponse_ToOpenAIResponse.
    [Fact]
    public void ModelConversion_AlwaysInstallsAChoiceList()
    {
        var empty = new NanogptResponse { Id = "test-1", Choices = [] }.ToOpenAiResponse();
        Assert.Empty(empty.Choices!);

        var single = new NanogptResponse
        {
            Choices =
            [
                new NanogptChoice
                {
                    Message = new NanogptMessage { Reasoning = "thinking..." },
                },
            ],
        }.ToOpenAiResponse();
        Assert.Equal("thinking...", Assert.Single(single.Choices!).Message!.ReasoningContent);

        var multiple = new NanogptResponse
        {
            Choices =
            [
                new NanogptChoice { Message = new NanogptMessage { Reasoning = "r1" } },
                new NanogptChoice { Message = new NanogptMessage { Reasoning = "r2" } },
                new NanogptChoice { Message = new NanogptMessage { Reasoning = "r3" } },
            ],
        }.ToOpenAiResponse();
        Assert.Equal(3, multiple.Choices!.Count);
    }

    // Reference TestChoice_ToOpenAIChoice.
    [Fact]
    public void ModelConversion_MapsMessageAndDeltaReasoning()
    {
        var choice = new NanogptChoice
        {
            Message = new NanogptMessage { Reasoning = "final reasoning" },
            Delta = new NanogptMessage { Reasoning = "partial reasoning" },
        }.ToOpenAiChoice();

        Assert.Equal("final reasoning", choice.Message!.ReasoningContent);
        Assert.Equal("partial reasoning", choice.Delta!.ReasoningContent);
    }

    // Reference TestMessage_ToOpenAIMessage.
    [Fact]
    public void ModelConversion_MessageWithoutReasoningKeepsItNull()
    {
        var message = new NanogptMessage().ToOpenAiMessage();

        Assert.Null(message.ReasoningContent);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static NanogptOutboundTransformer Transformer()
        => new(new NanogptOutboundConfig
        {
            BaseUrl = "https://nano-gpt.com/api/v1",
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

    private static LlmRequest SimpleRequest()
        => new()
        {
            Model = "zai-org/glm-4.7:thinking",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static HttpResponseData Response(string body)
        => new()
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(body),
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIChatCompletion },
        };
}
