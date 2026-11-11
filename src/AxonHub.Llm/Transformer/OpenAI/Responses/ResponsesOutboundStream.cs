using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of responsesOutboundStream (outbound_stream.go:40-945): turns OpenAI
/// Responses API SSE events into the unified LLM response stream.
/// </summary>
/// <remarks>
/// The reference drives its state machine through recursive <c>Next()</c>
/// calls; the port uses an equivalent loop. A synthetic <c>[DONE]</c> is
/// emitted only after a real terminal event, and a later transport error after
/// that terminal must not surface.
/// </remarks>
internal sealed class ResponsesOutboundStream : IStream<LlmResponse>
{
    private readonly IStream<StreamEvent> _stream;

    // Event queue.
    private List<LlmResponse> _eventQueue = [];
    private int _queueIndex;
    private Exception? _err;

    // Track whether the response reached a real terminal event. A synthetic or
    // provider [DONE] marker is valid only after this becomes true.
    private bool _responseCompleted;
    private bool _doneEmitted;

    // Response metadata.
    private string _responseId = string.Empty;
    private string _responseModel = string.Empty;
    private string? _previousResponseId;
    private Usage? _usage;
    private long _created;

    // Content accumulation.
    private readonly StringBuilder _textContent = new();
    private readonly StringBuilder _reasoningContent = new();

    // Tool call tracking.
    private readonly Dictionary<string, ToolCall> _toolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _itemToCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _toolCallIndex = new(StringComparer.Ordinal);

    // Reasoning signature tracking.
    private readonly Dictionary<string, string?> _pendingReasoningEncryptedContent =
        new(StringComparer.Ordinal);

    // Transformer metadata tracking.
    private readonly Dictionary<string, JsonElement> _transformerMetadata =
        new(StringComparer.Ordinal);
    private bool _transformerMetadataEmitted;
    private Dictionary<string, List<string>> _responseHeaders = [];

    public ResponsesOutboundStream(IStream<StreamEvent> stream)
    {
        _stream = stream;
    }

    public bool Next()
    {
        while (true)
        {
            // If we have events in the queue, return them first.
            if (_queueIndex < _eventQueue.Count)
            {
                return true;
            }

            // Clear the queue and reset the index for new events.
            _eventQueue = [];
            _queueIndex = 0;

            // The terminal event contains the final outcome and any usage. Once
            // its queued chunks are drained, finish without reading a later
            // transport error.
            if (_responseCompleted)
            {
                if (!_doneEmitted)
                {
                    _doneEmitted = true;
                    _eventQueue.Add(LlmResponse.DoneResponse);
                    return true;
                }

                return false;
            }

            // Try to get the next chunk from the source.
            if (!_stream.Next())
            {
                if (_err is null && _stream.Err is null)
                {
                    _err = LlmErrors.StreamIncomplete;
                }

                return false;
            }

            var streamEvent = _stream.Current;
            try
            {
                TransformStreamChunk(streamEvent);
            }
            catch (Exception ex)
            {
                _err = ex;
                return false;
            }
        }
    }

    /// <summary>
    /// Port of <c>transformStreamChunk</c>. Events are enqueued instead of
    /// being returned; a thrown error becomes the stream's terminal error.
    /// </summary>
    private void TransformStreamChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            return;
        }

        if (streamEvent.Headers is { Count: > 0 } headers)
        {
            _responseHeaders = ResponsesStreamMetadata.CloneHeaders(headers);
        }

        if (streamEvent.Data is not { Length: > 0 })
        {
            return;
        }

        // A bare [DONE] is only a transport marker. It never proves semantic
        // completion, and it must not stop source consumption: a decoder or
        // network error may only become visible when the source is advanced to
        // exhaustion. Clean EOF without a semantic terminal is classified by
        // Next().
        if (streamEvent.Data.AsSpan().SequenceEqual("[DONE]"u8))
        {
            return;
        }

        // Parse the streaming event.
        ResponsesStreamEvent ev;
        try
        {
            // Go's Unmarshal accepts a literal null and leaves the zero struct.
            ev = GoStructJson.RootKind(streamEvent.Data) == "null"
                ? new ResponsesStreamEvent()
                : LlmJson.Deserialize<ResponsesStreamEvent>(streamEvent.Data)
                    ?? throw new JsonException("unexpected null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal responses api stream event: "
                + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        if (ev.Type.Length == 0 && streamEvent.Type == ResponsesStreamEventTypes.ResponseMetadata)
        {
            ev.Type = ResponsesStreamEventTypes.ResponseMetadata;
        }

        // Build the base response.
        var resp = new LlmResponse
        {
            Object = "chat.completion.chunk",
            Id = _responseId,
            Model = _responseModel,
            Created = _created,
            PreviousResponseId = _previousResponseId,
        };

        if (_responseHeaders.Count > 0)
        {
            resp.TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [ResponsesKeys.ResponseHeadersTransformerMetadataKey] =
                    ResponsesStreamMetadata.EncodeTransportHeaders(_responseHeaders),
            };
        }

        if (ev.Type == ResponsesStreamEventTypes.ResponseMetadata)
        {
            resp.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            resp.TransformerMetadata[ResponsesKeys.ResponseMetadataTransformerMetadataKey] =
                ResponsesStreamMetadata.EncodeRawEvent(streamEvent.Data);
            Enqueue(resp);
            return;
        }

        switch (ev.Type)
        {
            case ResponsesStreamEventTypes.ResponseCreated:
                if (ev.Response is { } created)
                {
                    _responseId = created.Id;
                    _responseModel = created.Model;
                    _created = created.CreatedAt;
                    _previousResponseId = created.PreviousResponseId;

                    resp.Id = _responseId;
                    resp.Model = _responseModel;
                    resp.Created = _created;
                    resp.PreviousResponseId = _previousResponseId;

                    if (created.Usage is not null)
                    {
                        _usage = created.Usage.ToUsage();
                        resp.Usage = _usage;
                    }
                }

                resp.Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message { Role = "assistant" },
                    },
                ];
                break;

            case ResponsesStreamEventTypes.ResponseInProgress:
                // Update state but don't emit an event.
                if (ev.Response is { } inProgress)
                {
                    _responseId = inProgress.Id;
                    _responseModel = inProgress.Model;
                    _created = inProgress.CreatedAt;
                    _previousResponseId = inProgress.PreviousResponseId;

                    if (inProgress.Usage is not null)
                    {
                        _usage = inProgress.Usage.ToUsage();
                    }
                }

                return;

            case ResponsesStreamEventTypes.OutputItemAdded:
                if (ev.Item is not { } addedItem)
                {
                    return;
                }

                switch (addedItem.Type)
                {
                    case "reasoning":
                        if (addedItem.Id.Length == 0
                            || addedItem.EncryptedContent is not { Length: > 0 })
                        {
                            return;
                        }

                        // Responses streams may send a provisional
                        // encrypted_content on item.added and the final blob on
                        // item.done. Hold the value until item.done so the final
                        // blob replaces the provisional one instead of being
                        // concatenated.
                        _pendingReasoningEncryptedContent[addedItem.Id] =
                            OpenAiSignatures.EncodeOpenAIEncryptedContent(addedItem.EncryptedContent);
                        return;

                    case "function_call":
                        {
                            // Initialize tool call tracking.
                            var toolCallIdx = _toolCalls.Count;
                            _toolCalls[addedItem.CallId] = new ToolCall
                            {
                                Id = addedItem.CallId,
                                Type = ToolTypes.Function,
                                Function = new FunctionCall
                                {
                                    Name = addedItem.Name,
                                    Namespace = ResponsesConvertUtil.NullIfEmpty(addedItem.Namespace),
                                    Arguments = string.Empty,
                                },
                            };

                            // Map item.id to call_id for later lookup.
                            _itemToCallId[addedItem.Id] = addedItem.CallId;
                            _toolCallIndex[addedItem.CallId] = toolCallIdx;

                            resp.Choices =
                            [
                                new Choice
                            {
                                Index = 0,
                                Delta = new Message
                                {
                                    ToolCalls =
                                    [
                                        new ToolCall
                                        {
                                            Id = ResponsesConvertUtil.NullIfEmpty(addedItem.CallId),
                                            Type = ToolTypes.Function,
                                            Index = toolCallIdx,
                                            Function = new FunctionCall
                                            {
                                                Name = addedItem.Name,
                                                Namespace = ResponsesConvertUtil.NullIfEmpty(
                                                    addedItem.Namespace),
                                            },
                                        },
                                    ],
                                },
                            },
                        ];
                            break;
                        }

                    case "custom_tool_call":
                        {
                            // Custom tool call - initialize tracking; the input will
                            // be streamed via delta events.
                            var toolCallIdx = _toolCalls.Count;
                            _toolCalls[addedItem.CallId] = new ToolCall
                            {
                                Id = addedItem.CallId,
                                Type = ToolTypes.ResponsesCustomTool,
                                ResponseCustomToolCall = new ResponseCustomToolCall
                                {
                                    CallId = addedItem.CallId,
                                    Name = addedItem.Name,
                                    Input = string.Empty,
                                },
                            };
                            _itemToCallId[addedItem.Id] = addedItem.CallId;
                            _toolCallIndex[addedItem.CallId] = toolCallIdx;

                            resp.Choices =
                            [
                                new Choice
                            {
                                Index = 0,
                                Delta = new Message
                                {
                                    ToolCalls =
                                    [
                                        new ToolCall
                                        {
                                            Id = ResponsesConvertUtil.NullIfEmpty(addedItem.CallId),
                                            Type = ToolTypes.ResponsesCustomTool,
                                            Index = toolCallIdx,
                                            ResponseCustomToolCall = new ResponseCustomToolCall
                                            {
                                                CallId = addedItem.CallId,
                                                Name = addedItem.Name,
                                            },
                                        },
                                    ],
                                },
                            },
                        ];
                            break;
                        }

                    default:
                        // For other item types (e.g., message), skip - no
                        // meaningful content to emit.
                        return;
                }

                break;

            case ResponsesStreamEventTypes.FunctionCallArgumentsDelta:
                if (ev.ItemId is { } deltaItemId)
                {
                    // Look up call_id from item_id mapping.
                    var callId = _itemToCallId.TryGetValue(deltaItemId, out var mappedCallId)
                        ? mappedCallId
                        : deltaItemId;

                    if (_toolCalls.TryGetValue(callId, out var tc))
                    {
                        var delta = ev.Delta ?? string.Empty;
                        tc.Function.Arguments += delta;
                        var toolCallIdx = _toolCallIndex[callId];

                        resp.Choices =
                        [
                            new Choice
                            {
                                Index = 0,
                                Delta = new Message
                                {
                                    ToolCalls =
                                    [
                                        new ToolCall
                                        {
                                            Index = toolCallIdx,
                                            Function = new FunctionCall { Arguments = delta },
                                        },
                                    ],
                                },
                            },
                        ];
                    }
                }

                break;

            case ResponsesStreamEventTypes.FunctionCallArgumentsDone:
                {
                    var callId = ev.CallId ?? string.Empty;
                    if (callId.Length == 0 && ev.ItemId is { } doneItemId)
                    {
                        _itemToCallId.TryGetValue(doneItemId, out var mappedCallId);
                        callId = mappedCallId ?? string.Empty;
                        if (callId.Length == 0)
                        {
                            // Fallback: item_id might be the call_id itself.
                            callId = doneItemId;
                        }
                    }

                    if (!_toolCalls.TryGetValue(callId, out var tc))
                    {
                        return; // Intentionally skip an unknown tool call.
                    }

                    var identityChanged = false;
                    if (!string.IsNullOrEmpty(ev.Name) && ev.Name != tc.Function.Name)
                    {
                        tc.Function.Name = ev.Name;
                        identityChanged = true;
                    }

                    if (!string.IsNullOrEmpty(ev.Namespace) && ev.Namespace != tc.Function.Namespace)
                    {
                        tc.Function.Namespace = ev.Namespace;
                        identityChanged = true;
                    }

                    // Some upstreams provide the complete JSON arguments only in the
                    // done event. Preserve arguments already emitted through delta
                    // events and forward only the missing suffix so downstream
                    // Responses streams receive the full value once.
                    var finalArgs = ev.Arguments ?? string.Empty;
                    var missingArgs = string.Empty;
                    if (finalArgs.Length == 0)
                    {
                        if (!identityChanged)
                        {
                            return; // An empty done event must not overwrite accumulated deltas.
                        }
                    }
                    else
                    {
                        var forwardedArgs = tc.Function.Arguments;
                        if (forwardedArgs.Length == 0)
                        {
                            missingArgs = finalArgs;
                        }
                        else if (finalArgs.StartsWith(forwardedArgs, StringComparison.Ordinal))
                        {
                            missingArgs = finalArgs[forwardedArgs.Length..];
                        }
                        else if (EqualJsonValues(forwardedArgs, finalArgs))
                        {
                            // The final payload may be reformatted without changing
                            // its meaning. The complete arguments were already
                            // forwarded, so do not emit a duplicate.
                            missingArgs = string.Empty;
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                "function call arguments mismatch for call_id "
                                + ResponsesCreatedAt.GoQuote(callId));
                        }

                        tc.Function.Arguments = finalArgs;
                        if (missingArgs.Length == 0 && !identityChanged)
                        {
                            return;
                        }
                    }

                    var toolCallIdx = _toolCallIndex[callId];
                    resp.Choices =
                    [
                        new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    Id = ResponsesConvertUtil.NullIfEmpty(tc.Id),
                                    Type = ResponsesConvertUtil.NullIfEmpty(tc.Type),
                                    Index = toolCallIdx,
                                    Function = new FunctionCall
                                    {
                                        Name = tc.Function.Name,
                                        Namespace = tc.Function.Namespace,
                                        Arguments = missingArgs,
                                    },
                                },
                            ],
                        },
                    },
                ];
                    break;
                }

            case ResponsesStreamEventTypes.CustomToolCallInputDelta:
                if (ev.ItemId is { } inputDeltaItemId)
                {
                    var callId = _itemToCallId.TryGetValue(inputDeltaItemId, out var mappedCallId)
                        ? mappedCallId
                        : inputDeltaItemId;

                    if (_toolCalls.TryGetValue(callId, out var tc)
                        && tc.ResponseCustomToolCall is { } custom)
                    {
                        var delta = ev.Delta ?? string.Empty;
                        custom.Input += delta;
                        var toolCallIdx = _toolCallIndex[callId];

                        resp.Choices =
                        [
                            new Choice
                            {
                                Index = 0,
                                Delta = new Message
                                {
                                    ToolCalls =
                                    [
                                        new ToolCall
                                        {
                                            Index = toolCallIdx,
                                            Type = ToolTypes.ResponsesCustomTool,
                                            ResponseCustomToolCall = new ResponseCustomToolCall
                                            {
                                                CallId = callId,
                                                Name = custom.Name,
                                                Input = delta,
                                            },
                                        },
                                    ],
                                },
                            },
                        ];
                    }
                }

                break;

            case ResponsesStreamEventTypes.CustomToolCallInputDone:
                if (ev.ItemId is null)
                {
                    return; // Intentionally skip an unassociated final input.
                }

                if (!_itemToCallId.TryGetValue(ev.ItemId, out var doneCallId))
                {
                    doneCallId = ev.ItemId;
                }

                if (!ReconcileCustomToolCallInput(resp, doneCallId, ev.Input ?? string.Empty))
                {
                    return;
                }

                break;

            case ResponsesStreamEventTypes.ContentPartAdded:
                return; // Intentionally skip this event.

            case ResponsesStreamEventTypes.OutputTextDelta:
                {
                    var delta = ev.Delta ?? string.Empty;
                    _textContent.Append(delta);

                    resp.Choices =
                    [
                        new Choice
                    {
                        Index = 0,
                        Delta = new Message { Content = MessageContent.FromText(delta) },
                    },
                ];
                    break;
                }

            case ResponsesStreamEventTypes.ReasoningSummaryTextDelta:
            case ResponsesStreamEventTypes.ReasoningTextDelta:
                {
                    var delta = ev.Delta ?? string.Empty;
                    _reasoningContent.Append(delta);

                    var itemId = ev.ItemId ?? string.Empty;
                    if (itemId.Length == 0)
                    {
                        return; // Intentionally skip an unassociated reasoning delta.
                    }

                    resp.TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        [ResponsesKeys.ReasoningItemTransformerMetadataKey] =
                            ResponsesStreamMetadata.EncodeReasoningItemMetadata(itemId),
                    };

                    resp.Choices =
                    [
                        new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Id = itemId,
                            ReasoningContent = delta,
                        },
                    },
                ];
                    break;
                }

            case ResponsesStreamEventTypes.OutputTextDone:
                // The content was already streamed via deltas.
                return;

            case ResponsesStreamEventTypes.ReasoningSummaryTextDone:
            case ResponsesStreamEventTypes.ReasoningTextDone:
                // The content was already streamed via deltas.
                return;

            case ResponsesStreamEventTypes.OutputItemDone:
                if (ev.Item is not { } doneItem)
                {
                    return;
                }

                if (doneItem.Type == "custom_tool_call")
                {
                    var callId = doneItem.CallId;
                    if (callId.Length == 0)
                    {
                        callId = _itemToCallId.TryGetValue(doneItem.Id, out var mappedCallId)
                            ? mappedCallId
                            : string.Empty;
                        if (callId.Length == 0)
                        {
                            callId = doneItem.Id;
                        }
                    }

                    if (doneItem.Input is null)
                    {
                        return; // Intentionally skip an item without a final input.
                    }

                    if (!ReconcileCustomToolCallInput(resp, callId, doneItem.Input))
                    {
                        return;
                    }

                    break;
                }

                if (doneItem.Type is "compaction" or "compaction_summary")
                {
                    resp.Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = ResponsesOutboundConvert.ConvertOutputToMessage(
                                [doneItem], _transformerMetadata),
                        },
                    ];
                    break;
                }

                if (doneItem.Type == "web_search_call")
                {
                    ResponsesOutboundConvert.AppendResponseWebSearchCallMetadata(
                        _transformerMetadata, doneItem);
                    return;
                }

                if (doneItem.Type == "reasoning")
                {
                    if (doneItem.Id.Length == 0)
                    {
                        return; // Intentionally skip this event.
                    }

                    var encryptedContent =
                        OpenAiSignatures.EncodeOpenAIEncryptedContent(doneItem.EncryptedContent);
                    if (string.IsNullOrEmpty(encryptedContent))
                    {
                        encryptedContent =
                            _pendingReasoningEncryptedContent.TryGetValue(
                                doneItem.Id, out var pending)
                                ? pending
                                : null;
                    }

                    _pendingReasoningEncryptedContent.Remove(doneItem.Id);
                    if (string.IsNullOrEmpty(encryptedContent))
                    {
                        return; // Intentionally skip this event.
                    }

                    resp.TransformerMetadata =
                        new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            [ResponsesKeys.ReasoningItemTransformerMetadataKey] =
                                ResponsesStreamMetadata.EncodeReasoningItemMetadata(
                                    doneItem.Id, done: true),
                        };
                    resp.Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message { ReasoningSignature = encryptedContent },
                        },
                    ];
                    break;
                }

                if (doneItem.Type != "message")
                {
                    return; // Intentionally skip this event.
                }

                var msg = ResponsesOutboundConvert.ConvertOutputToMessage(
                    [doneItem], _transformerMetadata);
                if (msg.Annotations is not { Count: > 0 } messageAnnotations)
                {
                    return; // Intentionally skip this event.
                }

                if (_transformerMetadata.Count > 0)
                {
                    resp.TransformerMetadata = _transformerMetadata;
                    _transformerMetadataEmitted = true;
                }

                resp.Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message { Annotations = messageAnnotations },
                    },
                ];
                break;

            case ResponsesStreamEventTypes.ContentPartDone:
            case ResponsesStreamEventTypes.ReasoningSummaryPartAdded:
            case ResponsesStreamEventTypes.ReasoningSummaryPartDone:
                // These events don't need special handling - skip.
                return;

            case ResponsesStreamEventTypes.ResponseCompleted:
                {
                    if (_responseCompleted)
                    {
                        return;
                    }

                    // Response completed - emit two events: one with finish_reason,
                    // one with usage.
                    _responseCompleted = true;
                    if (ev.Response is { } completed)
                    {
                        _previousResponseId = completed.PreviousResponseId;
                        resp.PreviousResponseId = _previousResponseId;
                    }

                    if (_transformerMetadata.Count > 0 && !_transformerMetadataEmitted)
                    {
                        resp.TransformerMetadata = _transformerMetadata;
                        _transformerMetadataEmitted = true;
                    }

                    // Some compatible providers report abnormal outcomes in
                    // response.completed instead of a separate terminal event.
                    // Preserve those statuses too.
                    var finishReason = string.Empty;
                    if (ev.Response is { Status: { } status })
                    {
                        switch (status)
                        {
                            case "incomplete":
                                // Distinguish truncation from content-filter
                                // rejection via the incomplete details the upstream
                                // attached to the response.
                                var reason = ev.Response.IncompleteDetails?.Reason ?? string.Empty;
                                finishReason = reason == "content_filter" ? "content_filter" : "length";
                                break;
                            case "failed":
                                finishReason = "error";
                                break;
                            case "cancelled":
                            case "canceled":
                                finishReason = "cancelled";
                                break;
                        }
                    }

                    if (finishReason.Length == 0)
                    {
                        finishReason = _toolCalls.Count > 0 ? "tool_calls" : "stop";
                    }

                    // First event: finish_reason with an empty delta.
                    resp.Choices =
                    [
                        new Choice
                    {
                        Index = 0,
                        Delta = new Message(),
                        FinishReason = finishReason,
                    },
                ];
                    break;
                }

            case ResponsesStreamEventTypes.ResponseFailed:
                if (_responseCompleted)
                {
                    return;
                }

                _responseCompleted = true;
                resp.Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        FinishReason = "error",
                    },
                ];
                break;

            case ResponsesStreamEventTypes.ResponseIncomplete:
                if (_responseCompleted)
                {
                    return;
                }

                _responseCompleted = true;
                var incompleteReason = "length";
                if (ev.Response?.IncompleteDetails?.Reason == "content_filter")
                {
                    incompleteReason = "content_filter";
                }

                resp.Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        FinishReason = incompleteReason,
                    },
                ];
                break;

            case ResponsesStreamEventTypes.ResponseCancelled:
                if (_responseCompleted)
                {
                    return;
                }

                _responseCompleted = true;
                resp.Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        FinishReason = "cancelled",
                    },
                ];
                break;

            case ResponsesStreamEventTypes.Error:
                {
                    var detail = new ErrorDetail
                    {
                        Code = ResponsesConvertUtil.NullIfEmpty(ev.Code),
                        Message = ev.Message ?? string.Empty,
                        Type = ev.Type,
                        Param = ResponsesConvertUtil.NullIfEmpty(ev.Param),
                    };

                    if (ev.Error is { } streamError)
                    {
                        if (streamError.Type is { Length: > 0 } errorType)
                        {
                            detail.Type = errorType;
                        }

                        if (streamError.Code is { Length: > 0 } errorCode)
                        {
                            detail.Code = errorCode;
                        }

                        if (streamError.Message.Length > 0)
                        {
                            detail.Message = streamError.Message;
                        }

                        if (streamError.Param is { Length: > 0 } errorParam)
                        {
                            detail.Param = errorParam;
                        }
                    }

                    throw new ResponseError
                    {
                        StatusCode = ev.Status,
                        Detail = detail,
                    };
                }

            case ResponsesStreamEventTypes.ImageGenerationPartialImage:
            case ResponsesStreamEventTypes.ImageGenerationGenerating:
            case ResponsesStreamEventTypes.ImageGenerationInProgress:
            case ResponsesStreamEventTypes.ImageGenerationCompleted:
                // Handle image generation events.
                if (!string.IsNullOrEmpty(ev.PartialImageB64))
                {
                    var imageUrl = Xurl.BuildDataUrl("image/png", ev.PartialImageB64, true);
                    resp.Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message
                            {
                                Content = MessageContent.FromParts(
                                [
                                    new MessageContentPart
                                    {
                                        Type = "image_url",
                                        ImageUrl = new ImageUrl { Url = imageUrl },
                                    },
                                ]),
                            },
                        },
                    ];
                }
                else
                {
                    resp.Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message(),
                        },
                    ];
                }

                break;

            default:
                // Unknown event type - skip.
                return;
        }

        if (_responseCompleted && ev.Response is { } terminal
            && (terminal.Error is not null || terminal.IncompleteDetails is not null))
        {
            resp.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            resp.TransformerMetadata[ResponsesKeys.TerminalDetailsTransformerMetadataKey] =
                ResponsesConvertUtil.ToElement(new ResponsesTerminalDetails
                {
                    Error = terminal.Error,
                    IncompleteDetails = terminal.IncompleteDetails,
                });
        }

        Enqueue(resp);

        // Preserve usage on every terminal outcome, after its finish_reason
        // chunk.
        if (_responseCompleted && ev.Response is { Usage: { } responseUsage })
        {
            _usage = responseUsage.ToUsage();
            Enqueue(new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = _responseId,
                Model = _responseModel,
                Created = _created,
                PreviousResponseId = _previousResponseId,
                Choices = [],
                Usage = _usage,
            });
        }
    }

    /// <summary>Port of <c>reconcileCustomToolCallInput</c>.</summary>
    private bool ReconcileCustomToolCallInput(
        LlmResponse resp, string callId, string finalInput)
    {
        if (!_toolCalls.TryGetValue(callId, out var tc) || tc.ResponseCustomToolCall is not { } custom)
        {
            return false;
        }

        if (finalInput.Length == 0)
        {
            return false; // An empty final event must not overwrite accumulated deltas.
        }

        var forwardedInput = custom.Input;
        string missingInput;
        if (forwardedInput.Length == 0)
        {
            missingInput = finalInput;
        }
        else if (finalInput.StartsWith(forwardedInput, StringComparison.Ordinal))
        {
            missingInput = finalInput[forwardedInput.Length..];
        }
        else
        {
            throw new InvalidOperationException(
                "custom tool call input mismatch for call_id "
                + ResponsesCreatedAt.GoQuote(callId));
        }

        custom.Input = finalInput;
        if (missingInput.Length == 0)
        {
            return false;
        }

        resp.Choices =
        [
            new Choice
            {
                Index = 0,
                Delta = new Message
                {
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Index = _toolCallIndex[callId],
                            Type = ToolTypes.ResponsesCustomTool,
                            ResponseCustomToolCall = new ResponseCustomToolCall
                            {
                                CallId = callId,
                                Name = custom.Name,
                                Input = missingInput,
                            },
                        },
                    ],
                },
            },
        ];

        return true;
    }

    /// <summary>Port of <c>equalJSONValues</c>.</summary>
    internal static bool EqualJsonValues(string left, string right)
    {
        if (!TryDecodeJsonValue(left, out var leftValue)
            || !TryDecodeJsonValue(right, out var rightValue))
        {
            return false;
        }

        return JsonValueEquals(leftValue, rightValue);
    }

    /// <summary>
    /// Port of <c>decodeJSONValue</c>: one JSON value only (a trailing value is
    /// an error), numbers compared by their literal lexeme.
    /// </summary>
    private static bool TryDecodeJsonValue(string value, out JsonElement result)
    {
        result = default;
        try
        {
            using var doc = JsonDocument.Parse(value);
            result = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Port of <c>reflect.DeepEqual</c> over decoded JSON values.</summary>
    private static bool JsonValueEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    // Go's decoder keeps the last duplicate key; JsonDocument keeps
                    // them all.
                    var leftMembers = LastWinsMembers(left);
                    var rightMembers = LastWinsMembers(right);
                    if (leftMembers.Count != rightMembers.Count)
                    {
                        return false;
                    }

                    foreach (var (name, value) in leftMembers)
                    {
                        if (!rightMembers.TryGetValue(name, out var other) || !JsonValueEquals(value, other))
                        {
                            return false;
                        }
                    }

                    return true;
                }

            case JsonValueKind.Array:
                {
                    var leftItems = left.EnumerateArray().ToArray();
                    var rightItems = right.EnumerateArray().ToArray();
                    if (leftItems.Length != rightItems.Length)
                    {
                        return false;
                    }

                    for (var i = 0; i < leftItems.Length; i++)
                    {
                        if (!JsonValueEquals(leftItems[i], rightItems[i]))
                        {
                            return false;
                        }
                    }

                    return true;
                }

            case JsonValueKind.Number:
                // Go's UseNumber keeps the literal lexeme; 1 and 1.0 differ.
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;

            default:
                return false;
        }
    }

    private static Dictionary<string, JsonElement> LastWinsMembers(JsonElement obj)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            members[property.Name] = property.Value;
        }

        return members;
    }

    private void Enqueue(LlmResponse resp)
    {
        _eventQueue.Add(resp);
    }

    public LlmResponse? Current
    {
        get
        {
            if (_queueIndex < _eventQueue.Count)
            {
                return _eventQueue[_queueIndex++];
            }

            return null;
        }
    }

    public Exception? Err
    {
        get
        {
            if (_err is not null)
            {
                return _err;
            }

            if (_responseCompleted)
            {
                return null;
            }

            return _stream.Err;
        }
    }

    public void Dispose() => _stream.Dispose();
}
