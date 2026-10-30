using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of llm/transformer/anthropic/outbound_stream.go: Anthropic Messages SSE
/// events become unified chat-completion chunks, keeping the tool-call and
/// thinking state across events.
/// </summary>
/// <remarks>
/// The reference wraps this stream in <c>streams.NoNil</c>; null results are
/// skipped here instead, which is observationally identical.
/// </remarks>
public sealed class AnthropicOutboundStream : IStream<LlmResponse>
{
    private readonly IStream<StreamEvent> _stream;
    private readonly StreamState _state;

    private LlmResponse? _current;
    private Exception? _err;

    public AnthropicOutboundStream(IStream<StreamEvent> stream, AnthropicPlatformType platformType)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _state = new StreamState(platformType);
    }

    public LlmResponse? Current => _current;

    public Exception? Err => _err ?? _stream.Err;

    public bool Next()
    {
        while (true)
        {
            if (_err is not null)
            {
                return false;
            }

            if (!_stream.Next())
            {
                return false;
            }

            LlmResponse? response;
            try
            {
                response = TransformStreamChunk(_stream.Current);
            }
            catch (Exception ex)
            {
                _err = ex;
                return false;
            }

            if (response is null)
            {
                // streams.NoNil: events that map to no response are dropped.
                continue;
            }

            _current = response;
            return true;
        }
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Port of filterStreamEvent.</summary>
    public static bool FilterStreamEvent(StreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        if (streamEvent.Data.Length == 0)
        {
            return false;
        }

        // Only events that contribute to the unified response survive.
        return streamEvent.Type is "message_start"
            or "content_block_start"
            or "content_block_delta"
            or "message_delta"
            or "message_stop"
            or "error";
    }

    /// <summary>Port of outboundStream.transformStreamChunk.</summary>
    public LlmResponse? TransformStreamChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            throw new InvalidOperationException("stream event is nil");
        }

        if (streamEvent.Data.Length == 0)
        {
            throw new InvalidOperationException("event data is empty");
        }

        if (IsDoneData(streamEvent.Data))
        {
            return LlmResponse.Done();
        }

        if (streamEvent.Type == "error")
        {
            throw ParseAnthropicStreamErrorEvent(streamEvent)
                ?? new ResponseError("stream error");
        }

        AnthropicStreamEvent evt;
        try
        {
            // Go's Unmarshal accepts a literal null and leaves the zero struct.
            evt = GoStructJson.RootKind(streamEvent.Data) == "null"
                ? new AnthropicStreamEvent()
                : LlmJson.Deserialize<AnthropicStreamEvent>(streamEvent.Data)
                    ?? throw new InvalidOperationException("unexpected null");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            var detail = ex is JsonException jsonException
                ? AnthropicInboundTransformer.JsonErrorText(jsonException)
                : "unexpected null";
            throw new InvalidOperationException(
                "failed to unmarshal anthropic stream event: " + detail,
                ex);
        }

        var response = new LlmResponse
        {
            Object = "chat.completion.chunk",
            Id = _state.StreamId, // Stored from message_start.
            Model = _state.StreamModel, // Stored from message_start.
            Created = 0,
        };

        switch (evt.Type)
        {
            case "message_start":
                TransformMessageStart(evt, response);
                break;

            case "content_block_start":
                if (!TransformContentBlockStart(evt, response))
                {
                    return null;
                }

                break;

            case "content_block_delta":
                if (!TransformContentBlockDelta(evt, response))
                {
                    return null;
                }

                break;

            case "message_delta":
                TransformMessageDelta(evt, response);
                break;

            case "message_stop":
                // Final event: an empty response marks completion.
                response.Choices = [];
                if (_state.StreamUsage is not null)
                {
                    response.Usage = _state.StreamUsage;
                }

                break;

            default:
                // Unreachable after filtering, but handle gracefully.
                throw new InvalidOperationException("unexpected stream event type: " + evt.Type);
        }

        return response;
    }

    private void TransformMessageStart(AnthropicStreamEvent evt, LlmResponse response)
    {
        if (evt.Message is { } message)
        {
            _state.StreamId = message.Id;
            _state.StreamModel = message.Model;

            response.Id = _state.StreamId;
            response.Model = _state.StreamModel;

            if (message.Usage is { } usage)
            {
                _state.StreamUsage = AnthropicAggregator.ConvertToLlmUsage(usage, _state.PlatformType);
                response.ServiceTier = string.IsNullOrEmpty(usage.ServiceTier) ? null : usage.ServiceTier;
                response.Usage = _state.StreamUsage;
            }
        }

        response.Choices =
        [
            new Choice
            {
                Index = 0,
                Delta = new Message { Role = "assistant" },
            },
        ];
    }

    /// <summary>
    /// Returns false when the block start maps to no response (NoNil drops it).
    /// </summary>
    private bool TransformContentBlockStart(AnthropicStreamEvent evt, LlmResponse response)
    {
        if (evt.ContentBlock is not { } block)
        {
            return false;
        }

        // Anthropic assigns a stable ordinal index to each content block;
        // preserve it so non-streaming and interleaving consumers can
        // reconstruct the original order.
        var blockIndex = evt.Index is { } index ? (int)index : -1;

        if (AnthropicJson.IsAnthropicToolUseLike(block.Type))
        {
            if (block.Name is null)
            {
                // Defensive; Anthropic always sends a name on *_tool_use.
                return false;
            }

            _state.ToolIndex++;

            var toolCall = new ToolCall
            {
                Index = _state.ToolIndex,
                Id = block.Id,
                Type = "function",
                Function = new FunctionCall
                {
                    Name = block.Name,
                    Arguments = string.Empty,
                },
            };
            toolCall.TransformerMetadata = AnthropicJson.SetSpecialMeta(
                toolCall.TransformerMetadata,
                block.Type,
                block.Caller);
            if (blockIndex >= 0)
            {
                toolCall.TransformerMetadata =
                    AnthropicJson.SetBlockIndex(toolCall.TransformerMetadata, blockIndex);
            }

            _state.ToolCalls[_state.ToolIndex] = toolCall;

            response.Choices =
            [
                new Choice
                {
                    Index = 0,
                    Delta = new Message
                    {
                        Role = "assistant",
                        ToolCalls = [toolCall],
                    },
                },
            ];
        }
        else if (AnthropicJson.IsAnthropicToolResultLike(block.Type))
        {
            // Server-side tool results arrive complete in content_block_start
            // and carry no subsequent deltas; emit them inline on the assistant
            // message.
            var inlineResult = AnthropicConvertToLlm.InlineToolResultFromBlock(block);
            if (blockIndex >= 0)
            {
                inlineResult.TransformerMetadata =
                    AnthropicJson.SetBlockIndex(inlineResult.TransformerMetadata, blockIndex);
            }

            response.Choices =
            [
                new Choice
                {
                    Index = 0,
                    Delta = new Message
                    {
                        Role = "assistant",
                        InlineToolResults = [inlineResult],
                    },
                },
            ];
        }
        else
        {
            // Other content block starts (text, thinking, ...) carry no data.
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns false when the delta maps to no response (NoNil drops it).
    /// </summary>
    private bool TransformContentBlockDelta(AnthropicStreamEvent evt, LlmResponse response)
    {
        if (evt.Delta is not { } delta)
        {
            return true; // choices stays empty
        }

        var choice = new Choice
        {
            Index = 0,
            Delta = new Message { Role = "assistant" },
        };

        // The reference dereferences *delta.Type and panics on null; this port
        // treats a missing type as a no-op (difference C057).
        switch (delta.Type)
        {
            case "input_json_delta":
                if (delta.PartialJson is { } partialJson)
                {
                    if (!_state.ToolCalls.TryGetValue(_state.ToolIndex, out var toolCall)
                        || toolCall is null)
                    {
                        // A tool_use-style delta arrived without a preceding
                        // content_block_start we registered (e.g. a block type
                        // we do not handle). Drop it rather than dereference
                        // null.
                        return false;
                    }

                    var deltaToolCall = new ToolCall
                    {
                        Index = _state.ToolIndex,
                        Id = toolCall.Id,
                        Type = "function",
                        Function = new FunctionCall
                        {
                            Arguments = partialJson,
                        },
                    };
                    if (toolCall.TransformerMetadata is { Count: > 0 } metadata)
                    {
                        deltaToolCall.TransformerMetadata = metadata;
                    }

                    response.Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message
                            {
                                Role = "assistant",
                                ToolCalls = [deltaToolCall],
                            },
                        },
                    ];

                    return true;
                }

                break;

            case "text_delta":
                if (delta.Text is not null)
                {
                    choice.Delta!.Content = MessageContent.FromText(delta.Text);
                }

                break;

            case "citations_delta":
                if (delta.Citation is null)
                {
                    return false;
                }

                if (AnthropicOutboundResponse.LlmAnnotationFromCitation(delta.Citation) is not { } annotation)
                {
                    return false;
                }

                choice.Delta!.Annotations = [annotation];
                break;

            case "thinking":
                return false;

            case "thinking_delta":
                choice.Delta!.ReasoningContent = delta.Thinking;
                break;

            case "signature_delta":
                choice.Delta!.ReasoningSignature =
                    AnthropicSignatures.EncodeAnthropicSignature(delta.Signature);
                break;
        }

        response.Choices = [choice];
        return true;
    }

    private void TransformMessageDelta(AnthropicStreamEvent evt, LlmResponse response)
    {
        // Final usage information.
        if (evt.Usage is not null)
        {
            var usage = AnthropicAggregator.ConvertToLlmUsage(evt.Usage, _state.PlatformType)
                ?? new Usage();

            if (_state.StreamUsage is { } previous)
            {
                if (usage.PromptTokens == 0 && previous.PromptTokens > 0)
                {
                    usage.PromptTokens = previous.PromptTokens;
                }

                if (usage.PromptTokensDetails is null && previous.PromptTokensDetails is not null)
                {
                    usage.PromptTokensDetails = previous.PromptTokensDetails;
                }
            }

            // Recompute the total after merging prompt/completion usage.
            usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;

            _state.StreamUsage = usage;
        }

        if (evt.Delta?.StopReason is not { } stopReason)
        {
            return;
        }

        string? finishReason = stopReason switch
        {
            "end_turn" => "stop",
            "max_tokens" => "length",
            "stop_sequence" => "stop",
            "tool_use" => "tool_calls",
            _ => stopReason,
        };

        // OpenAI-compatible clients expect a delta object on every chunk,
        // including the finish_reason chunk.
        response.Choices =
        [
            new Choice
            {
                Index = 0,
                Delta = new Message(),
                FinishReason = finishReason,
            },
        ];
    }

    /// <summary>Port of parseAnthropicStreamErrorEvent.</summary>
    public static ResponseError? ParseAnthropicStreamErrorEvent(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            return null;
        }

        if (streamEvent.Data.Length == 0)
        {
            return new ResponseError
            {
                Detail = new ErrorDetail
                {
                    Message = "stream error",
                    Type = "stream_error",
                },
            };
        }

        var (root, parseFailed) = ParseRoot(streamEvent.Data);

        JsonElement? candidate = root;
        if (!parseFailed
            && root is { ValueKind: JsonValueKind.Object } rootObject
            && GJsonString(Get(rootObject, "event")) == "error"
            && Get(rootObject, "data") is { } data)
        {
            candidate = data;
        }

        // zai-style: {"error":{"code":"...","message":"..."},"request_id":"..."}
        // Anthropic: {"type":"error","error":{"type":"...","message":"..."},"request_id":"..."}
        var errorObject = Get(candidate, "error");
        var detail = new ErrorDetail
        {
            Code = NullIfEmpty(GJsonString(Get(errorObject, "code"))),
            Message = GJsonString(Get(errorObject, "message")),
            Type = GJsonString(Get(errorObject, "type")),
            Param = NullIfEmpty(GJsonString(Get(errorObject, "param"))),
        };

        if (detail.Message.Length == 0)
        {
            detail.Message = GJsonString(Get(candidate, "message"));
        }

        if (detail.Message.Length == 0 && errorObject is not null)
        {
            detail.Message = GJsonString(errorObject);
        }

        if (detail.Message.Length == 0)
        {
            detail.Message = "stream error";
        }

        var requestId = GJsonString(Get(candidate, "request_id"));
        if (requestId.Length == 0)
        {
            requestId = GJsonString(Get(errorObject, "request_id"));
        }

        detail.RequestId = NullIfEmpty(requestId);

        if (detail.Type.Length == 0 && GJsonString(Get(candidate, "type")) == "error")
        {
            detail.Type = "stream_error";
        }

        return new ResponseError { Detail = detail };
    }

    private static (JsonElement? Root, bool Failed) ParseRoot(byte[] data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        }
        catch (JsonException)
        {
            // gjson tolerates many malformed documents; the port's strict parser
            // reports them as "no fields", which yields the same fallback text
            // but not the same type marker (difference C058).
            return (null, true);
        }

        using (document)
        {
            return (document.RootElement.Clone(), false);
        }
    }

    private static bool IsDoneData(byte[] data)
    {
        ReadOnlySpan<byte> done = "[DONE]"u8;
        return data.AsSpan().SequenceEqual(done);
    }

    /// <summary>gjson <c>Get</c>: a missing path or a non-object parent yields no value.</summary>
    private static JsonElement? Get(JsonElement? root, string name)
        => root is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty(name, out var property)
            ? property
            : null;

    /// <summary>gjson <c>Result.String()</c>.</summary>
    private static string GJsonString(JsonElement? element)
    {
        if (element is not { } value)
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText(),
        };
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>Port of streamState.</summary>
    private sealed class StreamState(AnthropicPlatformType platformType)
    {
        public string StreamId { get; set; } = string.Empty;

        public string StreamModel { get; set; } = string.Empty;

        public Usage? StreamUsage { get; set; }

        public AnthropicPlatformType PlatformType { get; } = platformType;

        public int ToolIndex { get; set; } = -1;

        public Dictionary<int, ToolCall> ToolCalls { get; } = [];
    }
}
