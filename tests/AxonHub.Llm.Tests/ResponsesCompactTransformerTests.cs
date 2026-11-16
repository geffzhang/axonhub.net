using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the Responses compaction surface
/// (llm/transformer/openai/responses/compact_inbound.go, compact_outbound.go,
/// compact.go). Expectations mirror compact_inbound_test.go and
/// compact_outbound_test.go.
/// </summary>
public sealed class ResponsesCompactTransformerTests
{
    private static readonly ResponsesCompactInboundTransformer Inbound = new();

    [Fact]
    public void CompactInboundRejectsNilAndEmpty()
    {
        Assert.Equal(
            "invalid request: http request is nil",
            Assert.Throws<TransformerException>(() => Inbound.TransformRequest(null)).Message);

        Assert.Equal(
            "invalid request: request body is empty",
            Assert.Throws<TransformerException>(
                () => Inbound.TransformRequest(new HttpRequestData { Body = [] })).Message);
    }

    [Fact]
    public void CompactInboundRequiresModel()
    {
        var ex = Assert.Throws<TransformerException>(
            () => Inbound.TransformRequest(JsonRequest("{\"instructions\":\"Be concise\"}")));

        Assert.Equal("invalid request: model is required", ex.Message);
    }

    [Fact]
    public void CompactInboundConvertsTextInput()
    {
        var request = Inbound.TransformRequest(JsonRequest("{\"model\":\"gpt-4o\",\"input\":\"Hello\"}"));

        Assert.Equal("gpt-4o", request.Model);
        Assert.Equal(RequestTypes.Compact, request.RequestType);
        Assert.Equal(ApiFormats.OpenAIResponseCompact, request.ApiFormat);
        Assert.False(request.Stream);
        Assert.Empty(request.Messages);

        Assert.NotNull(request.Compact);
        var message = Assert.Single(request.Compact!.Input!);
        Assert.Equal("user", message.Role);
        Assert.Equal("Hello", message.Content!.Content);
        Assert.Null(request.Compact.Instructions);
        Assert.Null(request.Compact.PromptCacheKey);
    }

    [Fact]
    public void CompactInboundConvertsInstructionsAndPromptCacheKey()
    {
        var request = Inbound.TransformRequest(JsonRequest(
            "{\"model\":\"gpt-4o\",\"input\":\"Hello\","
            + "\"instructions\":\"Be concise\",\"prompt_cache_key\":\"cache_key_1\"}"));

        Assert.Equal("Be concise", request.Compact!.Instructions);
        Assert.Equal("cache_key_1", request.Compact.PromptCacheKey);
    }

    [Fact]
    public void CompactOutboundBuildsCompactRequest()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com/v1", "test-key");
        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            RequestType = RequestTypes.Compact,
            Compact = new CompactRequest
            {
                Input = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
            },
        });

        Assert.Equal("POST", httpReq.Method);
        Assert.Equal("https://api.openai.com/v1/responses/compact", httpReq.Url);
        Assert.Equal(RequestTypes.Compact, httpReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIResponseCompact, httpReq.ApiFormat);
        Assert.True(httpReq.SkipInboundQueryMerge);
        Assert.Null(httpReq.Metadata);

        var payload = LlmJson.Deserialize<CompactApiRequest>(httpReq.Body!)!;
        Assert.Equal("gpt-4o", payload.Model);
        Assert.NotNull(payload.Input);
        var item = Assert.Single(payload.Input!.Items!);
        Assert.Equal("message", item.Type);
        Assert.Equal("user", item.Role);
    }

    [Fact]
    public void CompactOutboundRequiresCompactData()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com/v1", "test-key");
        var ex = Assert.Throws<InvalidOperationException>(() => transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-4o",
            RequestType = RequestTypes.Compact,
        }));

        Assert.Equal("compact request is nil in llm.Request", ex.Message);
    }

    [Fact]
    public void CompactOutboundBuildsCompactUrl()
    {
        Assert.Equal(
            "https://api.openai.com/v1/responses/compact",
            ResponsesOutboundTransformer.Create("https://api.openai.com/v1", "test-key").BuildCompactUrl());

        Assert.Equal(
            "https://custom-api.example.com/v1",
            ResponsesOutboundTransformer.Create("https://custom-api.example.com/v1##", "test-key").BuildCompactUrl());
    }

    [Fact]
    public void CompactRoundTripPreservesOfficialOptionalFields()
    {
        var inbound = Inbound.TransformRequest(JsonRequest(
            """
            {
              "model": "gpt-5.6",
              "previous_response_id": "resp_123",
              "prompt_cache_options": {"mode": "implicit", "ttl": "30m"},
              "prompt_cache_retention": "in_memory",
              "service_tier": "auto"
            }
            """));

        var outbound = ResponsesOutboundTransformer.Create("https://api.openai.com/v1", "test-key");
        var httpReq = outbound.TransformRequest(inbound);

        using var body = JsonDocument.Parse(httpReq.Body!);
        Assert.False(body.RootElement.TryGetProperty("input", out _));
        Assert.Equal("resp_123", body.RootElement.GetProperty("previous_response_id").GetString());
        Assert.Equal(
            "implicit",
            body.RootElement.GetProperty("prompt_cache_options").GetProperty("mode").GetString());
        Assert.Equal(
            "30m",
            body.RootElement.GetProperty("prompt_cache_options").GetProperty("ttl").GetString());
        Assert.Equal("in_memory", body.RootElement.GetProperty("prompt_cache_retention").GetString());
        Assert.Equal("auto", body.RootElement.GetProperty("service_tier").GetString());
    }

    [Fact]
    public void CompactOutboundTransformsCompactResponse()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com/v1", "test-key");
        var response = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Request = new HttpRequestData { RequestType = RequestTypes.Compact },
            Body = Bytes(
                """
                {
                  "id": "resp_001",
                  "object": "response.compaction",
                  "created_at": 1764967971,
                  "model": "gpt-4o",
                  "instructions": "Be concise",
                  "output": [
                    {
                      "id": "rs_001",
                      "type": "reasoning",
                      "status": "completed",
                      "summary": [{"type": "summary_text", "text": "Reasoning summary"}],
                      "encrypted_content": "gAAAAAB..."
                    },
                    {
                      "id": "msg_000",
                      "type": "message",
                      "status": "completed",
                      "content": [{"type": "output_text", "text": "Hello"}],
                      "role": "assistant"
                    }
                  ],
                  "usage": {
                    "input_tokens": 139,
                    "input_tokens_details": {"cached_tokens": 0},
                    "output_tokens": 438,
                    "output_tokens_details": {"reasoning_tokens": 64},
                    "total_tokens": 577
                  }
                }
                """),
        });

        Assert.Equal(RequestTypes.Compact, response.RequestType);
        Assert.Equal(ApiFormats.OpenAIResponseCompact, response.ApiFormat);
        Assert.Equal("gpt-4o", response.Model);
        Assert.Equal("resp_001", response.Id);
        Assert.Equal(1764967971, response.Created);
        Assert.Equal("response.compaction", response.Object);
        Assert.Empty(response.Choices);

        Assert.NotNull(response.Compact);
        Assert.Equal("resp_001", response.Compact!.Id);
        Assert.Equal(1764967971, response.Compact.CreatedAt);
        Assert.Equal("response.compaction", response.Compact.Object);
        Assert.Equal("Be concise", response.Compact.Instructions);

        var message = Assert.Single(response.Compact.Output);
        Assert.Equal("msg_000", message.Id);
        Assert.Equal("assistant", message.Role);
        Assert.Equal("Hello", message.Content!.Content);
        Assert.Equal("Reasoning summary", message.ReasoningContent);
        Assert.Equal("gAAAAAB...", message.ReasoningSignature);

        Assert.NotNull(response.Usage);
        Assert.Equal(139, response.Usage!.PromptTokens);
        Assert.Equal(438, response.Usage.CompletionTokens);
        Assert.Equal(577, response.Usage.TotalTokens);
        Assert.NotNull(response.Usage.CompletionTokensDetails);
        Assert.Equal(64, response.Usage.CompletionTokensDetails!.ReasoningTokens);
    }

    [Fact]
    public void CompactOutboundReportsHttpErrorEnvelope()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com/v1", "test-key");
        var ex = Assert.Throws<ResponseError>(() => transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 400,
            Request = new HttpRequestData { RequestType = RequestTypes.Compact },
            Body = Bytes("{\"error\":{\"message\":\"Invalid model\",\"type\":\"invalid_request_error\"}}"),
        }));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Invalid model", ex.Detail.Message);
        Assert.Equal("invalid_request_error", ex.Detail.Type);
    }

    [Fact]
    public void CompactOutboundRejectsEmptyBody()
    {
        var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com/v1", "test-key");
        var ex = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Request = new HttpRequestData { RequestType = RequestTypes.Compact },
            Body = [],
        }));

        Assert.Equal("response body is empty", ex.Message);
    }

    [Fact]
    public void CompactInboundRejectsNilAndMissingCompactData()
    {
        Assert.Equal(
            "compact response is nil",
            Assert.Throws<InvalidOperationException>(() => Inbound.TransformResponse(null)).Message);

        Assert.Equal(
            "compact response missing compact data",
            Assert.Throws<InvalidOperationException>(() => Inbound.TransformResponse(new LlmResponse())).Message);
    }

    [Fact]
    public void CompactInboundTransformsResponse()
    {
        var response = Inbound.TransformResponse(new LlmResponse
        {
            RequestType = RequestTypes.Compact,
            ApiFormat = ApiFormats.OpenAIResponseCompact,
            Model = "gpt-4o",
            Compact = new CompactResponse
            {
                Id = "resp_001",
                CreatedAt = 1764967971,
                Object = "response.compaction",
                Instructions = "Be concise",
                Output =
                [
                    new Message
                    {
                        Id = "msg_001",
                        Role = "assistant",
                        Content = MessageContent.FromText("Hello"),
                        ReasoningSignature = "gAAAAAB...",
                    },
                ],
            },
            Usage = new Usage { PromptTokens = 139, CompletionTokens = 438, TotalTokens = 577 },
        });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(response.Headers, "Content-Type"));
        Assert.Equal("no-cache", HttpClientUtils.HeaderGet(response.Headers, "Cache-Control"));

        var body = LlmJson.Deserialize<CompactApiResponse>(response.Body!)!;
        Assert.Equal("resp_001", body.Id);
        Assert.Equal(1764967971, body.CreatedAt);
        Assert.Equal("response.compaction", body.Object);
        Assert.Equal("gpt-4o", body.Model);
        Assert.Equal("Be concise", body.Instructions);

        Assert.NotNull(body.Output);
        Assert.Equal(2, body.Output!.Count);
        Assert.Equal("reasoning", body.Output[0].Type);
        Assert.Equal("gAAAAAB...", body.Output[0].EncryptedContent);

        var message = body.Output[1];
        Assert.Equal("message", message.Type);
        Assert.Equal("msg_001", message.Id);
        Assert.Equal("output_text", message.Content!.Items![0].Type);
        Assert.Equal("Hello", message.Content.Items[0].Text);

        Assert.NotNull(body.Usage);
        Assert.Equal(139, body.Usage!.InputTokens);
        Assert.Equal(438, body.Usage.OutputTokens);
        Assert.Equal(577, body.Usage.TotalTokens);
    }

    [Fact]
    public void CompactInboundTransformsCompactionSummaryResponse()
    {
        var response = Inbound.TransformResponse(new LlmResponse
        {
            RequestType = RequestTypes.Compact,
            ApiFormat = ApiFormats.OpenAIResponseCompact,
            Compact = new CompactResponse
            {
                Id = "resp_002",
                CreatedAt = 1764968000,
                Object = "response.compaction",
                Output =
                [
                    new Message
                    {
                        Id = "cmp_msg_001",
                        Role = "assistant",
                        Content = MessageContent.FromParts(
                        [
                            new MessageContentPart
                            {
                                Id = "cmp_001",
                                Type = "compaction_summary",
                                Compact = new CompactContent
                                {
                                    Id = "cmp_001",
                                    EncryptedContent = "encrypted_summary",
                                },
                            },
                        ]),
                    },
                ],
            },
        });

        var body = LlmJson.Deserialize<CompactApiResponse>(response.Body!)!;
        var item = Assert.Single(body.Output!);
        Assert.Equal("compaction_summary", item.Type);
        Assert.Equal("cmp_001", item.Id);
        Assert.Equal("encrypted_summary", item.EncryptedContent);
    }

    [Fact]
    public void CompactStreamingIsRejected()
    {
        var streamError = Assert.Throws<TransformerException>(
            () => Inbound.TransformStream(new LlmErrorAfterStream()));
        Assert.Equal("invalid request: compact does not support streaming", streamError.Message);

        var aggregateError = Assert.Throws<InvalidOperationException>(
            () => Inbound.AggregateStreamChunks([], out _));
        Assert.Equal("compact does not support streaming", aggregateError.Message);
    }

    [Fact]
    public void CompactTransformErrorDelegatesToResponsesMapping()
    {
        var response = Inbound.TransformError(TransformerErrors.InvalidRequest("bad input"));

        Assert.Equal(400, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body!);
        Assert.Equal(
            "invalid_request_error",
            body.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

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
