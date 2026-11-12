using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.AiSdk;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the AI SDK data stream inbound transformer
/// (llm/transformer/aisdk/datastream.go, convert_request.go, convert_stream.go
/// and steam.go). Covers the playground chat path: request conversion, the
/// stateful stream translation, the aggregated UIMessage and the error bodies.
/// </summary>
public sealed class AiSdkDataStreamTransformerTests
{
    private static readonly AiSdkDataStreamTransformer Transformer = new();

    // ------------------------------------------------------------------
    // TransformRequest
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequestMapsUiMessagesAndTools()
    {
        const string body = """
            {
              "model": "gpt-4o",
              "system": "be concise",
              "stream": true,
              "temperature": 0.4,
              "max_tokens": 128,
              "messages": [
                {
                  "role": "user",
                  "parts": [
                    { "type": "text", "text": "hi" },
                    { "type": "file", "mediaType": "image/png", "url": "https://example.test/a.png" }
                  ]
                },
                {
                  "role": "assistant",
                  "parts": [
                    { "type": "text", "text": "hello" },
                    { "type": "reasoning", "text": "thinking" }
                  ]
                }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "search",
                    "description": "search docs",
                    "parameters": { "type": "object", "properties": { "q": { "type": "string" } } }
                  }
                }
              ]
            }
            """;

        var request = Transformer.TransformRequest(new HttpRequestData
        {
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal("gpt-4o", request.Model);
        Assert.True(request.Stream);
        Assert.Equal(0.4, request.Temperature!.Value);
        Assert.Equal(128L, request.MaxTokens);
        Assert.Equal(ApiFormats.AiSDKDataStream, request.ApiFormat);
        Assert.Equal(RequestTypes.Chat, request.RequestType);
        Assert.Equal(3, request.Messages.Count);

        var system = request.Messages[0];
        Assert.Equal("system", system.Role);
        Assert.Equal("be concise", system.Content!.Content);

        var user = request.Messages[1];
        Assert.Equal("user", user.Role);
        Assert.NotNull(user.Content!.MultipleContent);
        Assert.Collection(
            user.Content.MultipleContent!,
            part =>
            {
                Assert.Equal("text", part.Type);
                Assert.Equal("hi", part.Text);
            },
            part =>
            {
                Assert.Equal("image_url", part.Type);
                Assert.Equal("https://example.test/a.png", part.ImageUrl!.Url);
            });

        // Reasoning parts are carried as plain text content (convert_request.go).
        var assistant = request.Messages[2];
        Assert.Equal("assistant", assistant.Role);
        Assert.NotNull(assistant.Content!.MultipleContent);
        Assert.Collection(
            assistant.Content.MultipleContent!,
            part => Assert.Equal("hello", part.Text),
            part => Assert.Equal("thinking", part.Text));

        var tool = Assert.Single(request.Tools!);
        Assert.Equal("function", tool.Type);
        Assert.Equal("search", tool.Function.Name);
        Assert.Equal("search docs", tool.Function.Description);
        Assert.Equal(
            "object",
            tool.Function.Parameters!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public void TransformRequestTreatsNullRootAsEmptyRequest()
    {
        var request = Transformer.TransformRequest(new HttpRequestData
        {
            Body = Encoding.UTF8.GetBytes("null"),
        });

        Assert.Equal(string.Empty, request.Model);
        Assert.Empty(request.Messages);
        Assert.True(request.Stream);
        Assert.Equal(ApiFormats.AiSDKDataStream, request.ApiFormat);
    }

    [Fact]
    public void TransformRequestRejectsMalformedJson()
    {
        var error = Assert.Throws<TransformerException>(() =>
            Transformer.TransformRequest(new HttpRequestData
            {
                Body = Encoding.UTF8.GetBytes("{"),
            }));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(
            "invalid request: failed to parse AI SDK request: unexpected end of JSON input",
            error.Message);
    }

    [Fact]
    public void TransformRequestRejectsNonObjectRoot()
    {
        var error = Assert.Throws<TransformerException>(() =>
            Transformer.TransformRequest(new HttpRequestData
            {
                Body = Encoding.UTF8.GetBytes("[1,2]"),
            }));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(
            "invalid request: failed to parse AI SDK request: "
            + "json: cannot unmarshal array into Go value of type aisdk.Request",
            error.Message);
    }

    [Fact]
    public void TransformRequestRejectsUnsupportedRole()
    {
        // convert_request.go returns a plain error for unknown roles; the
        // playground surfaces it as a generic 500 in both implementations.
        const string body = """
            {"messages":[{"role":"developer","parts":[{"type":"text","text":"x"}]}]}
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            Transformer.TransformRequest(new HttpRequestData
            {
                Body = Encoding.UTF8.GetBytes(body),
            }));

        Assert.Equal("unsupported role: developer", error.Message);
    }

    [Fact]
    public void TransformResponseRejectsNonStreamingResponses()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer.TransformResponse(new LlmResponse()));

        Assert.Equal(
            "data stream protocol only supports streaming responses",
            error.Message);
    }

    // ------------------------------------------------------------------
    // TransformStream
    // ------------------------------------------------------------------

    [Fact]
    public void TransformStreamEmitsTextProtocolSequence()
    {
        var events = Collect(
            new LlmResponse
            {
                Id = "msg_1",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = MessageContent.FromText("Hel"),
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_1",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = MessageContent.FromText("lo"),
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_1",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message { Role = "assistant" },
                        FinishReason = "stop",
                    },
                ],
            });

        Assert.Equal(
            new List<string>
            {
                "start",
                "start-step",
                "text-start",
                "text-delta",
                "text-delta",
                "text-end",
                "finish-step",
                "finish",
            },
            events.Select(e => e.Type));

        Assert.Equal("msg_1", events[0].MessageId);

        var textStart = events[2];
        Assert.StartsWith("text_", textStart.Id);
        Assert.Equal(textStart.Id, events[3].Id);
        Assert.Equal("Hel", events[3].Delta);
        Assert.Equal(textStart.Id, events[4].Id);
        Assert.Equal("lo", events[4].Delta);
        Assert.Equal(textStart.Id, events[5].Id);
    }

    [Fact]
    public void TransformStreamClosesReasoningBeforeText()
    {
        var events = Collect(
            new LlmResponse
            {
                Id = "msg_2",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            ReasoningContent = "think",
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_2",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = MessageContent.FromText("answer"),
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_2",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message { Role = "assistant" },
                        FinishReason = "stop",
                    },
                ],
            });

        Assert.Equal(
            new List<string>
            {
                "start",
                "start-step",
                "reasoning-start",
                "reasoning-delta",
                "reasoning-end",
                "finish-step",
                "start-step",
                "text-start",
                "text-delta",
                "text-end",
                "finish-step",
                "finish",
            },
            events.Select(e => e.Type));

        Assert.StartsWith("reasoning_", events[2].Id);
        Assert.Equal(events[2].Id, events[3].Id);
        Assert.Equal("think", events[3].Delta);
        Assert.Equal(events[2].Id, events[4].Id);
        Assert.StartsWith("text_", events[7].Id);
        Assert.Equal(events[7].Id, events[8].Id);
    }

    [Fact]
    public void TransformStreamEmitsToolInputEvents()
    {
        var events = Collect(
            new LlmResponse
            {
                Id = "msg_3",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    Index = 0,
                                    Id = "call_1",
                                    Type = "function",
                                    Function = new FunctionCall { Name = "search" },
                                },
                            ],
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_3",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    Index = 0,
                                    Id = "call_1",
                                    Type = "function",
                                    Function = new FunctionCall
                                    {
                                        Name = "search",
                                        Arguments = "{\"q\":\"x\"}",
                                    },
                                },
                            ],
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_3",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message { Role = "assistant" },
                        FinishReason = "tool_calls",
                    },
                ],
            });

        Assert.Equal(
            new List<string>
            {
                "start",
                "tool-input-start",
                "tool-input-delta",
                "finish-step",
                "finish",
            },
            events.Select(e => e.Type));

        Assert.Equal("call_1", events[1].ToolCallId);
        Assert.Equal("search", events[1].ToolName);
        Assert.Equal("call_1", events[2].ToolCallId);
        Assert.Equal("{\"q\":\"x\"}", events[2].InputTextDelta);
    }

    [Fact]
    public void TransformStreamAppendsDoneMarker()
    {
        using var transformed = Transformer.TransformStream(
            new SliceStream<LlmResponse>([
                new LlmResponse
                {
                    Id = "msg_4",
                    Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message
                            {
                                Role = "assistant",
                                Content = MessageContent.FromText("x"),
                            },
                            FinishReason = "stop",
                        },
                    ],
                },
            ]));

        var raw = new List<StreamEvent>();
        while (transformed.Next())
        {
            raw.Add(transformed.Current!);
        }

        Assert.Null(transformed.Err);
        var done = raw[^1];
        Assert.Equal(string.Empty, done.Type);
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(done.Data));
    }

    // ------------------------------------------------------------------
    // AggregateStreamChunks
    // ------------------------------------------------------------------

    [Fact]
    public void AggregateStreamChunksBuildsUiMessage()
    {
        var chunks = Events(
            new AiSdkStreamEvent { Type = "start", MessageId = "msg_1" },
            new AiSdkStreamEvent { Type = "text-start" },
            new AiSdkStreamEvent { Type = "text-delta", Delta = "Hello" },
            new AiSdkStreamEvent { Type = "text-delta", Delta = " world" },
            new AiSdkStreamEvent { Type = "text-end" },
            new AiSdkStreamEvent { Type = "reasoning-start" },
            new AiSdkStreamEvent { Type = "reasoning-delta", Delta = "thought" },
            new AiSdkStreamEvent { Type = "reasoning-end" },
            new AiSdkStreamEvent { Type = "finish" });
        chunks.Add(new StreamEvent { Type = string.Empty, Data = "[DONE]"u8.ToArray() });

        var body = Transformer.AggregateStreamChunks(chunks, out var meta);

        Assert.Equal("msg_1", meta.Id);
        Assert.Equal(
            "{\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":null,"
            + "\"parts\":[{\"type\":\"text\",\"text\":\"Hello world\"},"
            + "{\"type\":\"reasoning\",\"text\":\"thought\"}]}",
            Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void AggregateStreamChunksWritesNullPartsWhenNoContent()
    {
        var chunks = Events(new AiSdkStreamEvent { Type = "start", MessageId = "msg_1" });

        var body = Transformer.AggregateStreamChunks(chunks, out var meta);

        Assert.Equal("msg_1", meta.Id);
        Assert.Equal(
            "{\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":null,\"parts\":null}",
            Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void AggregateStreamChunksOmitsIdWithoutStartEvent()
    {
        var chunks = Events(new AiSdkStreamEvent { Type = "text-start" });

        var body = Transformer.AggregateStreamChunks(chunks, out var meta);

        Assert.Equal(string.Empty, meta.Id);

        // The dangling text block is flushed even when empty; Go's `text`
        // omitempty then leaves just the type marker.
        Assert.Equal(
            "{\"role\":\"assistant\",\"content\":null,\"parts\":[{\"type\":\"text\"}]}",
            Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void AggregateStreamChunksSkipsNonJsonAndInvalidEvents()
    {
        var chunks = Events(
            new AiSdkStreamEvent { Type = "start", MessageId = "msg_1" },
            new AiSdkStreamEvent { Type = "text-start" },
            new AiSdkStreamEvent { Type = "text-delta", Delta = "ok" },
            new AiSdkStreamEvent { Type = "text-end" });
        chunks.Add(new StreamEvent { Type = "message", Data = "not-json"u8.ToArray() });

        var body = Transformer.AggregateStreamChunks(chunks, out _);

        Assert.Equal(
            "{\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":null,"
            + "\"parts\":[{\"type\":\"text\",\"text\":\"ok\"}]}",
            Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void AggregateStreamChunksOfTransformStreamOutputMatchesEvents()
    {
        using var transformed = Transformer.TransformStream(
            new SliceStream<LlmResponse>([
                new LlmResponse
                {
                    Id = "msg_5",
                    Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message
                            {
                                Role = "assistant",
                                Content = MessageContent.FromText("Hello"),
                            },
                        },
                    ],
                },
                new LlmResponse
                {
                    Id = "msg_5",
                    Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message { Role = "assistant" },
                            FinishReason = "stop",
                        },
                    ],
                },
            ]));

        var chunks = new List<StreamEvent>();
        while (transformed.Next())
        {
            chunks.Add(transformed.Current!);
        }

        var body = Transformer.AggregateStreamChunks(chunks, out var meta);

        Assert.Equal("msg_5", meta.Id);
        Assert.Equal(
            "{\"id\":\"msg_5\",\"role\":\"assistant\",\"content\":null,"
            + "\"parts\":[{\"type\":\"text\",\"text\":\"Hello\"}]}",
            Encoding.UTF8.GetString(body));
    }

    // ------------------------------------------------------------------
    // TransformError
    // ------------------------------------------------------------------

    [Fact]
    public void TransformErrorMapsNilTo500()
    {
        var response = Transformer.TransformError(null);

        Assert.Equal(500, response.StatusCode);
        Assert.Equal(
            "{\"message\":\"internal server error\",\"type\":\"internal_server_error\"}",
            Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public void TransformErrorPassesThroughHttpClientError()
    {
        var body = "{\"error\":{\"message\":\"upstream\"}}"u8.ToArray();
        var response = Transformer.TransformError(
            new HttpClientError("POST", "https://upstream.test/v1/chat", 429, "429 Too Many Requests", body));

        Assert.Equal(429, response.StatusCode);
        Assert.Equal(body, response.Body);
    }

    [Fact]
    public void TransformErrorTrimsInvalidRequestPrefix()
    {
        var response = Transformer.TransformError(
            TransformerErrors.InvalidRequest("failed to parse AI SDK request: boom"));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(
            "{\"message\":\"failed to parse AI SDK request: boom\",\"type\":\"invalid_request\"}",
            Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public void TransformErrorUsesResponseErrorDetail()
    {
        var response = Transformer.TransformError(new ResponseError
        {
            StatusCode = 429,
            Detail = new ErrorDetail { Message = "slow down", Type = "rate_limit_error" },
        });

        Assert.Equal(429, response.StatusCode);
        Assert.Equal(
            "{\"message\":\"slow down\",\"type\":\"rate_limit_error\"}",
            Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public void TransformErrorFallsBackTo500WithMessage()
    {
        var response = Transformer.TransformError(new InvalidOperationException("boom"));

        Assert.Equal(500, response.StatusCode);
        Assert.Equal(
            "{\"message\":\"boom\",\"type\":\"internal_server_error\"}",
            Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public void TransformErrorInterpolatesMessageWithoutEscaping()
    {
        // datastream.go uses fmt.Appendf with the raw message, so quotes are not
        // escaped; the port reproduces the same (invalid) JSON literal.
        var response = Transformer.TransformError(new InvalidOperationException("say \"hi\""));

        Assert.Equal(
            "{\"message\":\"say \"hi\"\",\"type\":\"internal_server_error\"}",
            Encoding.UTF8.GetString(response.Body!));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static List<AiSdkStreamEvent> Collect(params LlmResponse[] responses)
    {
        using var transformed = Transformer.TransformStream(
            new SliceStream<LlmResponse>(responses));

        var events = new List<AiSdkStreamEvent>();
        while (transformed.Next())
        {
            var current = transformed.Current!;
            if (Encoding.UTF8.GetString(current.Data) == "[DONE]")
            {
                continue;
            }

            events.Add(LlmJson.Deserialize<AiSdkStreamEvent>(current.Data)!);
        }

        Assert.Null(transformed.Err);
        return events;
    }

    private static List<StreamEvent> Events(params AiSdkStreamEvent[] events)
    {
        var chunks = new List<StreamEvent>(events.Length);
        foreach (var streamEvent in events)
        {
            chunks.Add(new StreamEvent
            {
                Type = string.Empty,
                Data = GoJson.Marshal(streamEvent),
            });
        }

        return chunks;
    }
}
