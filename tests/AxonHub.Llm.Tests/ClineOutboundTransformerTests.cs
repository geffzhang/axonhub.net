using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Cline;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Cline outbound (llm/transformer/cline and its
/// outbound_test.go): the OpenAI wire format with reasoning, the
/// X-Client-Type marker, the empty-content normalization, the
/// {success,data,error,errors} envelope, and the Cline-specific aggregation.
/// </summary>
public sealed class ClineOutboundTransformerTests
{
    [Fact]
    public void TransformRequest_UsesConfiguredEndpointPath()
    {
        var transformer = new ClineOutboundTransformer(new ClineOutboundConfig
        {
            BaseUrl = "https://api.cline.bot/api/v1",
            EndpointPath = "/custom/chat/completions",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        var request = transformer.TransformRequest(Request("cline-pass/deepseek-v4-flash"));

        Assert.Equal("POST", request.Method);
        Assert.Equal("https://api.cline.bot/api/v1/custom/chat/completions", request.Url);
    }

    [Theory]
    [InlineData("cline-pass/deepseek-v4-flash")]
    [InlineData("cline-free/glm-5.2")]
    [InlineData("zai/glm-5.2")]
    public void TransformRequest_IdentifiesAsClineCli(string model)
    {
        var request = Transformer().TransformRequest(Request(model));

        Assert.Equal("POST", request.Method);
        Assert.Equal("cline-cli", Assert.Single(request.Headers!["X-Client-Type"]));
        Assert.Equal(ApiFormats.OpenAIChatCompletion, request.ApiFormat);
        Assert.Equal(model, Body(request).GetProperty("model").GetString());
    }

    [Fact]
    public void TransformRequest_NormalizesOnlyRequiredEmptyContent()
    {
        var request = new LlmRequest
        {
            Model = "cline-pass/deepseek-v4-flash",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText(string.Empty) },
                new Message { Role = "system" },
                new Message { Role = "developer", Content = MessageContent.FromParts([]) },
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromParts(
                        [new MessageContentPart { Type = "text", Text = string.Empty }]),
                },
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromParts(
                        [new MessageContentPart { Type = "text" }]),
                },
                new Message { Role = "assistant", Content = MessageContent.FromText(string.Empty) },
                new Message { Role = "tool", Content = MessageContent.FromText(string.Empty) },
                new Message { Role = "user", Content = MessageContent.FromText("   ") },
            ],
        };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal("cline-cli", Assert.Single(httpRequest.Headers!["X-Client-Type"]));

        var messages = Body(httpRequest).GetProperty("messages");
        Assert.Equal(8, messages.GetArrayLength());

        const string emptyTextPart = """[{"type":"text","text":""}]""";
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(emptyTextPart, messages[i].GetProperty("content").GetRawText());
        }

        Assert.Equal(string.Empty, messages[5].GetProperty("content").GetString());
        Assert.Equal(string.Empty, messages[6].GetProperty("content").GetString());
        Assert.Equal("   ", messages[7].GetProperty("content").GetString());
    }

    [Fact]
    public void TransformRequest_PatchesUnifiedMessageIndicesLikeTheReference()
    {
        // The reference merges every system message into the first one
        // (mergeSystemMessages) before building the wire body, but patches that
        // body at the *unified* message indices. An index past the merged tail
        // is appended, and sjson pads with nulls when the gap is larger than
        // one. This lock documents that quirk (C100).
        var request = new LlmRequest
        {
            Model = "cline-pass/deepseek-v4-flash",
            Messages =
            [
                new Message { Role = "system" },
                new Message { Role = "user", Content = MessageContent.FromText("a") },
                new Message { Role = "system" },
                new Message { Role = "user", Content = MessageContent.FromText("b") },
                new Message { Role = "system" },
            ],
        };

        var messages = Body(Transformer().TransformRequest(request)).GetProperty("messages");

        // The merged body is [system, user a, user b]; the patches run at the
        // unified indices 0, 2 and 4.
        const string emptyTextPart = """[{"type":"text","text":""}]""";
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal(emptyTextPart, messages[0].GetProperty("content").GetRawText());
        Assert.Equal("a", messages[1].GetProperty("content").GetString());
        // The second system message is gone from the merged body, so its patch
        // rewrote user b (unified index 3) instead.
        Assert.Equal(emptyTextPart, messages[2].GetProperty("content").GetRawText());
        // The third system message (unified index 4) lies two slots past the
        // merged tail, so sjson inserts one null first.
        Assert.Equal(JsonValueKind.Null, messages[3].ValueKind);
        Assert.Equal(emptyTextPart, messages[4].GetProperty("content").GetRawText());
    }

    [Fact]
    public void TransformResponse_UnwrapsClineData()
    {
        var response = Transformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """
                {
                    "success": true,
                    "data": {
                        "id": "chatcmpl-1",
                        "object": "chat.completion",
                        "created": 123,
                        "model": "cline-pass/deepseek-v4-flash",
                        "choices": [{
                            "index": 0,
                            "message": {
                                "role": "assistant",
                                "content": "ok"
                            },
                            "finish_reason": "stop"
                        }],
                        "usage": {
                            "prompt_tokens": 10,
                            "completion_tokens": 2,
                            "total_tokens": 12
                        }
                    }
                }
                """),
        });

        Assert.Equal("chatcmpl-1", response.Id);
        Assert.Equal("chat.completion", response.Object);
        Assert.Equal(123, response.Created);
        Assert.Equal("cline-pass/deepseek-v4-flash", response.Model);

        var choice = Assert.Single(response.Choices);
        Assert.NotNull(choice.Message);
        Assert.Equal("ok", choice.Message!.Content!.Content);
        Assert.Equal("stop", choice.FinishReason);

        Assert.NotNull(response.Usage);
        Assert.Equal(10, response.Usage!.PromptTokens);
        Assert.Equal(2, response.Usage.CompletionTokens);
        Assert.Equal(12, response.Usage.TotalTokens);
    }

    [Fact]
    public void TransformResponse_NullChoiceAndReasoningDetailDecodeAsZeroValues()
    {
        var response = Transformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """
                {
                    "success": true,
                    "data": {
                        "id": "chatcmpl-1",
                        "object": "chat.completion",
                        "created": 123,
                        "model": "cline-pass/deepseek-v4-flash",
                        "choices": [
                            null,
                            {
                                "index": 0,
                                "message": {
                                    "role": "assistant",
                                    "content": "",
                                    "reasoning_details": [null, {"type": "reasoning.text", "text": "think"}]
                                },
                                "finish_reason": "stop"
                            }
                        ]
                    }
                }
                """),
        });

        Assert.Equal(2, response.Choices.Count);

        var zero = response.Choices[0];
        Assert.Equal(0L, zero.Index);
        Assert.Null(zero.Message);
        Assert.Null(zero.FinishReason);

        Assert.Equal("think", response.Choices[1].Message!.ReasoningContent);
    }

    [Fact]
    public void TransformResponse_UnwrapsBareOpenAiBody()
    {
        // Without a success envelope the reference decodes the body as the
        // response itself.
        var response = Transformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """
                {
                    "id": "chatcmpl-2",
                    "object": "chat.completion",
                    "created": 456,
                    "model": "cline-pass/kimi-k3",
                    "choices": [{
                        "index": 0,
                        "message": {"role": "assistant", "content": "bare"},
                        "finish_reason": "stop"
                    }]
                }
                """),
        });

        Assert.Equal("chatcmpl-2", response.Id);
        Assert.Equal(
            "bare",
            Assert.Single(response.Choices).Message!.Content!.Content);
    }

    [Fact]
    public void TransformResponse_ReturnsClineErrorEnvelope()
    {
        var error = Assert.Throws<ResponseError>(() => Transformer().TransformResponse(
            new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes(
                    """{"success": false, "error": "model not found"}"""),
            }));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("model not found", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void TransformResponse_UsesErrorsFallbackForClineFailure()
    {
        var error = Assert.Throws<ResponseError>(() => Transformer().TransformResponse(
            new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes(
                    """{"success": false, "errors": [{"message": "quota exceeded"}]}"""),
            }));

        Assert.Equal(502, error.StatusCode);
        Assert.Contains("quota exceeded", error.Detail.Message, StringComparison.Ordinal);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void TransformResponse_UsesDefaultMessageForBareClineFailure()
    {
        var error = Assert.Throws<ResponseError>(() => Transformer().TransformResponse(
            new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("""{"success": false}"""),
            }));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("Cline request failed", error.Detail.Message);
    }

    [Fact]
    public void TransformResponse_MapsHttpStatusToClineError()
    {
        var error = Assert.Throws<ResponseError>(() => Transformer().TransformResponse(
            new HttpResponseData
            {
                StatusCode = 404,
                Body = Encoding.UTF8.GetBytes(
                    """{"success":false,"error":"model not found"}"""),
            }));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("model not found", error.Detail.Message);
    }

    [Fact]
    public void TransformResponse_RejectsEmptyAndMissingBodies()
    {
        var nilResponse = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(null));
        Assert.Equal("http response is nil", nilResponse.Message);

        var emptyBody = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(new HttpResponseData { StatusCode = 200 }));
        Assert.Equal("response body is empty", emptyBody.Message);
    }

    [Fact]
    public void TransformError_ParsesStringError()
    {
        var error = Transformer().TransformError(new HttpClientError(
            "POST",
            "https://api.cline.bot/api/v1/chat/completions",
            404,
            "404 Not Found",
            Encoding.UTF8.GetBytes("""{"success":false,"error":"model not found"}""")));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("model not found", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void TransformError_FallsBackToStatusText()
    {
        var error = Transformer().TransformError(new HttpClientError(
            "POST",
            "https://api.cline.bot/api/v1/chat/completions",
            418,
            "418 I'm a teapot",
            Encoding.UTF8.GetBytes("not json")));

        Assert.Equal(418, error.StatusCode);
        Assert.Equal("I'm a teapot", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void TransformError_NullUsesInternalServerError()
    {
        var error = Transformer().TransformError(null);

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Internal Server Error", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void TransformStreamChunk_ReturnsClineInBandError()
    {
        var error = Assert.Throws<ResponseError>(() => Transformer().TransformStreamChunk(
            new StreamEvent
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"success":false,"error":"response_format json_object is not supported"}"""),
            }));

        Assert.Equal("response_format json_object is not supported", error.Detail.Message);
    }

    [Fact]
    public void TransformStreamChunk_UsesErrorsFallbackForClineFailure()
    {
        var error = Assert.Throws<ResponseError>(() => Transformer().TransformStreamChunk(
            new StreamEvent
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"success":false,"errors":[{"message":"quota exceeded"}]}"""),
            }));

        Assert.Equal(502, error.StatusCode);
        Assert.Contains("quota exceeded", error.Detail.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformStreamChunk_ReturnsDoneAndRejectsNil()
    {
        Assert.Same(
            LlmResponse.DoneResponse,
            Transformer().TransformStreamChunk(new StreamEvent { Data = "[DONE]"u8.ToArray() }));

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformStreamChunk(null));
        Assert.Equal("stream event is nil", error.Message);
    }

    [Fact]
    public void TransformStream_MapsEventsThroughTheEnvelope()
    {
        var events = new List<StreamEvent>
        {
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"success":true,"data":{"id":"x","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"ok"}}]}}"""),
            },
            new() { Data = "[DONE]"u8.ToArray() },
        };

        using var stream = Transformer().TransformStream(
            null,
            new SliceStream<StreamEvent>(events));

        var responses = GoStreams.All(stream, out var error);

        Assert.Null(error);
        Assert.Equal(2, responses.Count);
        Assert.Equal("ok", Assert.Single(responses[0].Choices).Delta!.Content!.Content);
        Assert.Same(LlmResponse.DoneResponse, responses[1]);
    }

    [Fact]
    public void AggregateStreamChunks_UsesClineReasoningFields()
    {
        var chunks = new List<StreamEvent>
        {
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":123,"model":"cline-pass/deepseek-v4-flash","choices":[{"index":0,"delta":{"role":"assistant"}}]}"""),
            },
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":123,"model":"cline-pass/deepseek-v4-flash","choices":[{"index":0,"delta":{"reasoning":"think ","reasoning_details":[{"type":"reasoning.text","text":"think "}]}}]}"""),
            },
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":123,"model":"cline-pass/deepseek-v4-flash","choices":[{"index":0,"delta":{"reasoning":"then answer","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}"""),
            },
        };

        var data = Transformer().AggregateStreamChunks(null, chunks, out var meta);

        Assert.Equal("chatcmpl-1", meta.Id);
        Assert.NotNull(meta.Usage);
        Assert.Equal(7, meta.Usage!.TotalTokens);

        var response = LlmJson.Deserialize<LlmResponse>(data);
        Assert.NotNull(response);
        var choice = Assert.Single(response!.Choices);
        Assert.NotNull(choice.Message);
        Assert.Equal("think then answer", choice.Message!.ReasoningContent);
        Assert.Equal("ok", choice.Message.Content!.Content);
    }

    [Fact]
    public void AggregateStreamChunks_IgnoresDoneChunk()
    {
        var chunks = new List<StreamEvent>
        {
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":123,"model":"cline-pass/deepseek-v4-flash","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""),
            },
            new() { Data = "[DONE]"u8.ToArray() },
        };

        var data = Transformer().AggregateStreamChunks(null, chunks, out var meta);

        Assert.Equal("chatcmpl-1", meta.Id);

        var response = LlmJson.Deserialize<LlmResponse>(data);
        Assert.NotNull(response);
        Assert.Equal(
            "ok",
            Assert.Single(response!.Choices).Message!.Content!.Content);
    }

    [Fact]
    public void AggregateStreamChunks_SkipsInBandErrorChunks()
    {
        // The reference aggregator continues past any chunk whose transform
        // fails, so an in-band Cline error does not poison the whole stream.
        var chunks = new List<StreamEvent>
        {
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"success":false,"error":"temporary failure"}"""),
            },
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":123,"model":"cline-pass/deepseek-v4-flash","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}"""),
            },
        };

        var data = Transformer().AggregateStreamChunks(null, chunks, out _);

        var response = LlmJson.Deserialize<LlmResponse>(data);
        Assert.NotNull(response);
        Assert.Equal(
            "ok",
            Assert.Single(response!.Choices).Message!.Content!.Content);
    }

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new ClineOutboundTransformer(null!));
        Assert.Equal(
            "invalid Cline transformer configuration: config cannot be nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new ClineOutboundTransformer(new ClineOutboundConfig
            {
                BaseUrl = "https://api.cline.bot/api/v1",
            }));
        Assert.StartsWith(
            "invalid Cline transformer configuration: ",
            missingKey.Message,
            StringComparison.Ordinal);
        Assert.Contains("API key provider is required", missingKey.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ClineOutboundTransformer Transformer()
        => new(new ClineOutboundConfig
        {
            BaseUrl = "https://api.cline.bot/api/v1",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

    private static LlmRequest Request(string model) => new()
    {
        Model = model,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("hello") }],
    };

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
