using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of llm/transformer/anthropic/inbound.go: the Anthropic Messages
/// inbound transformer (<c>POST /v1/messages</c>,
/// <c>POST /anthropic/v1/messages</c>).
/// </summary>
/// <remarks>
/// The streaming half of the reference (inbound_stream.go and aggregator.go) is
/// not ported yet: <c>stream: true</c> requests are rejected with an
/// invalid-request error, registered in the difference ledger.
/// </remarks>
public sealed class AnthropicInboundTransformer : IInboundTransformer
{
    /// <summary>Port of TransformRequest: client HTTP request to unified request.</summary>
    public LlmRequest TransformRequest(HttpRequestData? request)
    {
        if (request is null)
        {
            throw TransformerErrors.InvalidRequest("http request is nil");
        }

        if (request.Body is null || request.Body.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("request body is empty");
        }

        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type");
        if (!contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        // Go validates the whole document before decoding, so syntax errors are
        // always reported with encoding/json's own wording.
        var syntaxError = GoJsonSyntax.Validate(request.Body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode anthropic request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(request.Body);
        AnthropicMessageRequest anthropicReq;
        if (rootKind is "null")
        {
            // null leaves the zero struct in place and falls into validation.
            anthropicReq = new AnthropicMessageRequest();
        }
        else
        {
            if (rootKind is not null)
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode anthropic request: "
                    + GoStructJson.TypeMismatch(rootKind, "anthropic.MessageRequest"));
            }

            // Go's decoder reports field-level type errors with encoding/json's
            // own wording. The walker runs first so the errors keep that text
            // and the saved-error precedence (a failing UnmarshalJSON aborts
            // and overrides an earlier type error).
            var typeError = AnthropicRequestTypeCheck.Validate(request.Body);
            if (typeError is not null)
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode anthropic request: " + typeError);
            }

            try
            {
                anthropicReq = LlmJson.Deserialize<AnthropicMessageRequest>(request.Body)
                    ?? new AnthropicMessageRequest();
            }
            catch (JsonException ex)
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode anthropic request: " + JsonErrorText(ex), ex);
            }
        }

        // Validate required fields (inbound.go:54-118).
        if (string.IsNullOrEmpty(anthropicReq.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (anthropicReq.Messages is null || anthropicReq.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("messages are required");
        }

        if ((anthropicReq.MaxTokens ?? 0) <= 0)
        {
            throw TransformerErrors.InvalidRequest("max_tokens is required and must be positive");
        }

        if (anthropicReq.System is { } system)
        {
            if (system.Prompt is null && system.MultiplePrompts is { Count: > 0 })
            {
                foreach (var prompt in system.MultiplePrompts)
                {
                    // A null array element decodes as the zero part, whose type
                    // is empty and fails this check just like Go's.
                    if (prompt?.Type != "text")
                    {
                        throw TransformerErrors.InvalidRequest("system prompt must be text");
                    }
                }
            }
        }

        if (anthropicReq.Thinking is { } thinking)
        {
            switch (thinking.Type)
            {
                case "disabled":
                    break;
                case "enabled":
                    // budget_tokens may be omitted (Z.AI/GLM-style clients); the
                    // default is resolved downstream.
                    if (thinking.BudgetTokens < 0)
                    {
                        throw TransformerErrors.InvalidRequest(
                            "budget_tokens must be positive when thinking type is enabled");
                    }

                    break;
                case "adaptive":
                    // output_config is optional for adaptive thinking.
                    if (anthropicReq.OutputConfig is { Effort.Length: > 0 } outputConfig)
                    {
                        switch (outputConfig.Effort)
                        {
                            case "low":
                            case "medium":
                            case "high":
                            case "xhigh":
                            case "max":
                                break;
                            default:
                                throw TransformerErrors.InvalidRequest(
                                    "output_config.effort must be one of: low, medium, high, xhigh, max");
                        }
                    }

                    break;
                default:
                    throw TransformerErrors.InvalidRequest(
                        "thinking.type must be one of: enabled, disabled, adaptive");
            }
        }

        if (anthropicReq.ToolChoice is { } toolChoice)
        {
            switch (toolChoice.Type)
            {
                case "auto":
                case "none":
                case "any":
                case "tool":
                    break;
                default:
                    throw TransformerErrors.InvalidRequest(
                        "tool_choice.type must be one of: auto, none, any, tool");
            }

            if (toolChoice.Type == "tool"
                && (toolChoice.Name is null || toolChoice.Name.Length == 0))
            {
                throw TransformerErrors.InvalidRequest(
                    "tool_choice.name is required when type is tool");
            }
        }

        // Deferred gap: the reference streams through inbound_stream.go and
        // aggregator.go, which are not ported yet. Reject after the reference
        // validations so only otherwise-valid streaming calls differ.
        if (anthropicReq.Stream is true)
        {
            throw TransformerErrors.InvalidRequest("streaming is not supported by this port yet");
        }

        return AnthropicConvertToLlm.ConvertToLlmRequest(anthropicReq);
    }

    /// <summary>Port of TransformResponse: unified response to the client HTTP response.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("chat completion response is nil");
        }

        var anthropicResp = AnthropicConvertFromLlm.ResponseFromLlm(response);
        var body = GoJson.Marshal(anthropicResp);

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Cache-Control"] = ["no-cache"],
            },
        };
    }

    /// <summary>Deferred gap: the reference's inbound stream transformer.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw new NotSupportedException("anthropic inbound streaming is not ported yet");

    /// <summary>Deferred gap: the reference's stream aggregator.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
        => throw new NotSupportedException("anthropic stream aggregation is not ported yet");

    /// <summary>
    /// Port of TransformError: maps orchestrator failures back to the Anthropic
    /// error envelope. The reference checks the sentinels in this order:
    /// invalid model, ResponseError, transport error, invalid request.
    /// </summary>
    public HttpResponseData TransformError(Exception? error)
    {
        if (error is null)
        {
            return ErrorResponse(500, "error", "internal server error", detailType: string.Empty);
        }

        var transformerError = GoErrors.As<TransformerException>(error);
        if (transformerError is { Kind: TransformerErrorKind.InvalidModel })
        {
            return ErrorResponse(422, "invalid_model_error", error.Message, detailType: string.Empty);
        }

        if (GoErrors.As<ResponseError>(error) is { } llmError)
        {
            return new HttpResponseData
            {
                StatusCode = llmError.StatusCode,
                Body = GoJson.Marshal(new AnthropicError
                {
                    Type = llmError.Detail.Type,
                    StatusCode = llmError.StatusCode,
                    RequestId = llmError.Detail.RequestId ?? string.Empty,
                    Error = new AnthropicErrorDetail
                    {
                        Type = llmError.Detail.Type,
                        Message = llmError.Detail.Message,
                    },
                }),
                Headers = JsonHeaders(),
            };
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            // The reference returns the transport error untouched; the API layer
            // writes its status and raw body.
            return new HttpResponseData
            {
                StatusCode = httpError.StatusCode,
                Body = httpError.Body,
                Headers = JsonHeaders(),
            };
        }

        if (transformerError is { Kind: TransformerErrorKind.InvalidRequest })
        {
            return ErrorResponse(400, "invalid_request_error", error.Message, "invalid_request_error");
        }

        return ErrorResponse(500, "internal_server_error", error.Message, "internal_server_error");
    }

    private static HttpResponseData ErrorResponse(
        int statusCode,
        string type,
        string message,
        string detailType)
        => new()
        {
            StatusCode = statusCode,
            Body = GoJson.Marshal(new AnthropicError
            {
                Type = type,
                StatusCode = statusCode,
                RequestId = string.Empty,
                Error = new AnthropicErrorDetail
                {
                    Type = detailType,
                    Message = message,
                },
            }),
            Headers = JsonHeaders(),
        };

    private static Dictionary<string, List<string>> JsonHeaders()
        => new(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        };

    /// <summary>
    /// Strips the decoder's position suffix so messages read like Go's.
    /// </summary>
    internal static string JsonErrorText(JsonException ex)
    {
        var message = ex.Message;
        var pathIndex = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return pathIndex > 0 ? message[..pathIndex] : message;
    }
}
