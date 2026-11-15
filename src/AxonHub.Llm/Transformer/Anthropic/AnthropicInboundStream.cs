using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of anthropicInboundStream (llm/transformer/anthropic/inbound_stream.go):
/// the stateful translation from the unified chat stream to Anthropic SSE
/// events, including thinking/signature buffering, tool-use accumulation,
/// citation deltas and terminal-event deduplication.
/// </summary>
internal sealed class AnthropicInboundStream : IStream<StreamEvent>
{
    private const string ReasoningItemMetadataKey = "openai_responses_reasoning_item";

    private readonly IStream<LlmResponse> _source;
    private readonly List<StreamEvent> _eventQueue = [];
    // Go: ToolCall.Index is int (64-bit), so the map key widens with it.
    private readonly Dictionary<long, ToolCall> _toolCalls = [];

    // Responses may interleave summary deltas from different reasoning items.
    // Keep those item-scoped buffers separate until their final signature arrives.
    private readonly Dictionary<string, List<string>> _pendingReasoningContent =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingReasoningSignatures =
        new(StringComparer.Ordinal);
    private readonly List<string> _pendingReasoningItemOrder = [];

    private bool _hasStarted;
    private bool _hasTextContentStarted;
    private bool _hasThinkingContentStarted;
    private bool _hasToolContentStarted;
    private bool _hasFinished;
    private bool _messageStopped;
    private string _messageId = string.Empty;
    private string _model = string.Empty;
    private long _contentIndex;
    private int _queueIndex;
    private Exception? _err;
    private string? _stopReason;
    private AnthropicUsage? _pendingUsage;

    // Tool call tracking.
    private long _currentToolCallIndex;
    private bool _hasCurrentToolCall;

    private string _lastEventType = string.Empty;

    // Buffered signature: when a signature arrives before thinking starts, hold
    // it until the thinking block finishes.
    // Go's pendingReasoningItemID is never assigned a non-empty value, so the
    // dead field is omitted here (difference ledger).
    private string? _pendingSignature;
    private bool _flushingReasoningItems;

    // Buffered citations for the currently open text block; emitted as
    // citations_delta events immediately before the block is closed.
    private List<AnthropicTextCitation> _pendingTextCitations = [];

    // Responses web_search_call metadata accumulates across chunks; emitted
    // Anthropic progress/result blocks are deduplicated by call ID.
    private Dictionary<string, ResponsesSearchState>? _responsesSearches;
    private List<ResponsesSearchSource> _responsesSearchCitations = [];

    public AnthropicInboundStream(IStream<LlmResponse> source)
    {
        _source = source;
    }

    public StreamEvent? Current
    {
        get
        {
            if (_queueIndex < _eventQueue.Count)
            {
                var streamEvent = _eventQueue[_queueIndex];
                _queueIndex++;
                return streamEvent;
            }

            return null;
        }
    }

    public Exception? Err => _err ?? _source.Err;

    public bool Next()
    {
        // If we have events in the queue, return them first.
        if (_queueIndex < _eventQueue.Count)
        {
            return true;
        }

        // Clear the queue and reset the index for new events.
        _eventQueue.Clear();
        _queueIndex = 0;

        if (!_source.Next())
        {
            if (_source.Err is not null || !_hasStarted || _messageStopped)
            {
                return false;
            }

            if (!_hasFinished)
            {
                if (ValidateToolArguments() is { } validationError)
                {
                    _err = Wrap("failed to validate tool arguments at stream end", validationError);
                    return false;
                }
            }

            if (EmitResponsesSearchBlocks(null, final: true) is { } finalSearchError)
            {
                _err = Wrap("failed to finalize search blocks", finalSearchError);
                return false;
            }

            if (CloseOpenContentBlocks() is { } closeError)
            {
                _err = Wrap("failed to close content blocks at stream end", closeError);
                return false;
            }

            if (_stopReason is null)
            {
                var stopReason = "end_turn";
                foreach (var toolCall in _toolCalls.Values)
                {
                    var anthropicType = AnthropicJson.GetAnthropicType(toolCall.TransformerMetadata);
                    if (anthropicType.Length == 0 || anthropicType == "tool_use")
                    {
                        stopReason = "tool_use";
                        break;
                    }
                }

                _stopReason = stopReason;
            }

            if (EnqueueTerminalEvents() is { } terminalError)
            {
                _err = Wrap("failed to finalize message at stream end", terminalError);
                return false;
            }

            return Next();
        }

        var chunk = _source.Current;

        // The terminal event must wait for the choices-less usage-only chunk
        // that carries the real counts. Some providers (e.g. ModelScope) attach
        // a zeroed usage object to every chunk, including the one carrying
        // finish_reason; finalizing there would emit the placeholder zeros and
        // drop the real counts arriving afterwards.
        if (chunk is not null && chunk.Usage is not null)
        {
            _pendingUsage = AnthropicConvertFromLlm.ConvertToAnthropicUsage(chunk.Usage);
        }

        if (chunk is null)
        {
            return Next();
        }

        if (chunk.Object == "[DONE]")
        {
            return Next();
        }

        if (_messageId.Length == 0 && chunk.Id.Length > 0)
        {
            _messageId = chunk.Id;
        }

        if (_model.Length == 0 && chunk.Model.Length > 0)
        {
            _model = chunk.Model;
        }

        if (!_hasStarted)
        {
            _hasStarted = true;

            // Usage is unknown until the provider reports it. The API layer can
            // add a client-only input estimate without contaminating persisted
            // usage (#2749).
            var usage = new AnthropicUsage();
            if (chunk.Usage is not null)
            {
                usage = AnthropicConvertFromLlm.ConvertToAnthropicUsage(chunk.Usage);
            }

            var messageStart = new AnthropicStreamEvent
            {
                Type = "message_start",
                Message = new AnthropicStreamMessage
                {
                    Id = _messageId,
                    Type = "message",
                    Role = "assistant",
                    Model = _model,
                    Content = [],
                    Usage = usage,
                },
            };

            if (EnqueueEvent(messageStart) is { } startError)
            {
                _err = Wrap("failed to enqueue message_start event", startError);
                return false;
            }
        }

        var finalSearchSnapshot = false;
        if (chunk.Choices.Count > 0)
        {
            var searchChoice = chunk.Choices[0];
            if (searchChoice.Message is { } searchMessage)
            {
                _responsesSearchCitations = AnthropicResponsesWebSearch.AppendCitations(
                    _responsesSearchCitations,
                    searchMessage.Annotations);
            }

            if (searchChoice.Delta is { } searchDelta)
            {
                _responsesSearchCitations = AnthropicResponsesWebSearch.AppendCitations(
                    _responsesSearchCitations,
                    searchDelta.Annotations);
            }

            finalSearchSnapshot = searchChoice.FinishReason is not null;
        }

        if (EmitResponsesSearchBlocks(chunk.TransformerMetadata, finalSearchSnapshot)
            is { } searchError)
        {
            _err = Wrap("failed to emit search blocks", searchError);
            return false;
        }

        if (chunk.Choices.Count > 0)
        {
            var choice = chunk.Choices[0];

            if (choice.Message?.Annotations is { Count: > 0 } messageAnnotations)
            {
                AppendPendingTextCitations(messageAnnotations, chunk.TransformerMetadata);
            }

            if (choice.Delta?.Annotations is { Count: > 0 } deltaAnnotations)
            {
                AppendPendingTextCitations(deltaAnnotations, chunk.TransformerMetadata);
            }

            var delta = choice.Delta;

            var reasoningContent = delta?.ReasoningContent;
            if (!string.IsNullOrEmpty(reasoningContent))
            {
                if (HandleReasoningContent(reasoningContent, chunk.TransformerMetadata) is { } reasoningError)
                {
                    _err = reasoningError;
                    return false;
                }
            }

            // Buffer the signature: defer emission to CloseThinkingBlock so
            // that each thinking block gets exactly one signature_delta.
            var reasoningSignature = delta?.ReasoningSignature;
            if (!string.IsNullOrEmpty(reasoningSignature))
            {
                if (HandleReasoningSignature(reasoningSignature, chunk.TransformerMetadata) is { } signatureError)
                {
                    _err = signatureError;
                    return false;
                }
            }

            var redactedReasoning = delta?.RedactedReasoningContent;
            if (!string.IsNullOrEmpty(redactedReasoning))
            {
                if (HandleRedactedReasoning(redactedReasoning) is { } redactedError)
                {
                    _err = redactedError;
                    return false;
                }
            }

            if (delta?.Content?.Content is { Length: > 0 } text)
            {
                if (HandleTextDelta(text) is { } textError)
                {
                    _err = textError;
                    return false;
                }
            }

            if (delta?.ToolCalls is { Count: > 0 } toolCalls)
            {
                if (HandleToolCalls(toolCalls) is { } toolError)
                {
                    _err = toolError;
                    return false;
                }
            }

            // Assistant-inlined tool results (Anthropic *_tool_result blocks
            // carried via llm.Message.InlineToolResults). Each one is emitted
            // as a complete content_block_start + content_block_stop pair,
            // since Anthropic delivers tool result blocks whole.
            if (delta?.InlineToolResults is { Count: > 0 } inlineResults)
            {
                if (HandleInlineToolResults(inlineResults) is { } inlineError)
                {
                    _err = inlineError;
                    return false;
                }
            }

            if (choice.FinishReason is { } finishReason && !_hasFinished)
            {
                if (HandleFinishReason(finishReason) is { } finishError)
                {
                    _err = finishError;
                    return false;
                }
            }
        }

        if (chunk.Usage is not null && chunk.Choices.Count == 0 && _hasFinished && !_messageStopped)
        {
            // Usage-only chunk after finish_reason: generate message_delta with
            // both the stop reason and the usage.
            if (EnqueueTerminalEvents() is { } usageError)
            {
                _err = usageError;
                return false;
            }
        }

        return Next();
    }

    public void Dispose() => _source.Dispose();

    /// <summary>
    /// Port of anthropicInboundStream.emitResponsesSearchBlocks: close any
    /// open client block, then emit each newly observed search as a complete
    /// server_tool_use + web_search_tool_result pair.
    /// </summary>
    private Exception? EmitResponsesSearchBlocks(
        Dictionary<string, JsonElement>? metadata,
        bool final)
    {
        _responsesSearches ??= new Dictionary<string, ResponsesSearchState>(StringComparer.Ordinal);
        var blocks = AnthropicResponsesWebSearch.Blocks(
            metadata,
            _responsesSearches,
            final,
            _responsesSearchCitations);
        if (blocks.Count == 0)
        {
            return null;
        }

        if (CloseOpenContentBlocks() is { } closeError)
        {
            return closeError;
        }

        foreach (var block in blocks)
        {
            var input = block.Input;
            var startBlock = block;
            if (block.Type == "server_tool_use")
            {
                startBlock = new AnthropicMessageContentBlock
                {
                    Type = block.Type,
                    Id = block.Id,
                    Name = block.Name,
                    Input = AnthropicJson.RawElement("{}"u8),
                };
            }

            if (EnqueueEvent(new AnthropicStreamEvent
                {
                    Type = "content_block_start",
                    Index = _contentIndex,
                    ContentBlock = startBlock,
                }) is { } startError)
            {
                return startError;
            }

            if (block.Type == "server_tool_use")
            {
                if (EnqueueEvent(new AnthropicStreamEvent
                    {
                        Type = "content_block_delta",
                        Index = _contentIndex,
                        Delta = new AnthropicStreamDelta
                        {
                            Type = "input_json_delta",
                            PartialJson = input?.GetRawText() ?? "{}",
                        },
                    }) is { } deltaError)
                {
                    return deltaError;
                }
            }

            if (EnqueueEvent(new AnthropicStreamEvent
                {
                    Type = "content_block_stop",
                    Index = _contentIndex,
                }) is { } stopError)
            {
                return stopError;
            }

            _contentIndex += 1;
        }

        return null;
    }

    private void AppendPendingTextCitations(
        List<Annotation> annotations,
        Dictionary<string, JsonElement>? metadata)
    {
        foreach (var annotation in annotations)
        {
            var citation = AnthropicConvertFromLlm.CitationFromLlmAnnotation(annotation, metadata);
            if (citation is null)
            {
                continue;
            }

            var key = AnthropicJson.CitationKey(citation);
            var exists = false;
            foreach (var existing in _pendingTextCitations)
            {
                if (AnthropicJson.CitationKey(existing) == key)
                {
                    exists = true;
                    break;
                }
            }

            if (exists)
            {
                continue;
            }

            _pendingTextCitations.Add(citation);
        }
    }

    private InvalidOperationException? FlushPendingTextCitations()
    {
        if (!_hasTextContentStarted || _pendingTextCitations.Count == 0)
        {
            return null;
        }

        foreach (var citation in _pendingTextCitations)
        {
            var streamEvent = new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = _contentIndex,
                Delta = new AnthropicStreamDelta
                {
                    Type = "citations_delta",
                    Citation = citation,
                },
            };

            if (EnqueueEvent(streamEvent) is { } error)
            {
                return Wrap("failed to enqueue citations_delta event", error);
            }
        }

        _pendingTextCitations = [];

        return null;
    }

    private InvalidOperationException? EmitBufferedReadToolArguments(long toolCallIndex)
    {
        if (!_toolCalls.TryGetValue(toolCallIndex, out var toolCall)
            || !AnthropicJson.IsReadToolName(toolCall.Function.Name))
        {
            return null;
        }

        var arguments = toolCall.Function.Arguments;
        if (arguments.Length == 0)
        {
            return null;
        }

        var (sanitized, complete) = AnthropicJson.NormalizeReadToolArguments(arguments);
        if (!complete)
        {
            sanitized = arguments;
        }

        var streamEvent = new AnthropicStreamEvent
        {
            Type = "content_block_delta",
            Index = _contentIndex,
            Delta = new AnthropicStreamDelta
            {
                Type = "input_json_delta",
                PartialJson = sanitized,
            },
        };

        if (EnqueueEvent(streamEvent) is { } error)
        {
            return Wrap("failed to enqueue buffered Read input_json_delta event", error);
        }

        return null;
    }

    private InvalidOperationException? EmitCurrentReadToolArguments()
        => _hasCurrentToolCall ? EmitBufferedReadToolArguments(_currentToolCallIndex) : null;

    private InvalidOperationException? CloseToolBlock()
    {
        if (!_hasToolContentStarted)
        {
            return null;
        }

        if (EmitCurrentReadToolArguments() is { } emitError)
        {
            return emitError;
        }

        _hasToolContentStarted = false;
        _hasCurrentToolCall = false;

        var stopEvent = new AnthropicStreamEvent
        {
            Type = "content_block_stop",
            Index = _contentIndex,
        };

        if (EnqueueEvent(stopEvent) is { } stopError)
        {
            return Wrap("failed to enqueue content_block_stop event", stopError);
        }

        _contentIndex += 1;

        return null;
    }

    private InvalidOperationException? ValidateToolArguments()
    {
        // The reference iterates a Go map, so with several invalid calls the
        // reported index is whatever the runtime visits first. Sorting keeps
        // the message deterministic (difference ledger).
        foreach (var index in _toolCalls.Keys.Order())
        {
            var toolCall = _toolCalls[index];
            if (toolCall is null || toolCall.Function.Arguments.Length == 0)
            {
                continue;
            }

            if (!IsJsonObject(toolCall.Function.Arguments))
            {
                return new InvalidOperationException(
                    $"invalid tool call arguments for tool call index {index}");
            }
        }

        return null;
    }

    /// <summary>
    /// Go <c>json.Unmarshal(args, &amp;map[string]json.RawMessage)</c>: valid JSON is
    /// not enough, the value must be an object (null decodes to a nil map and
    /// is rejected by the reference's <c>input == nil</c> check).
    /// </summary>
    private static bool IsJsonObject(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures any open or implied thinking block is properly closed:
    /// 1. a pending signature without a started block creates a synthetic empty
    ///    thinking block (start + signature_delta + stop);
    /// 2. an open thinking block flushes the pending signature (or a generated
    ///    placeholder) as signature_delta, then stops;
    /// 3. neither is a no-op.
    /// </summary>
    private Exception? CloseThinkingBlock()
    {
        if (!_flushingReasoningItems && _pendingReasoningItemOrder.Count > 0)
        {
            _flushingReasoningItems = true;
            var flushError = FlushBufferedReasoningItems();
            _flushingReasoningItems = false;
            if (flushError is not null)
            {
                return flushError;
            }
        }

        if (_pendingSignature is not null && !_hasThinkingContentStarted)
        {
            var signature = _pendingSignature;
            _pendingSignature = null;

            // Close any previously open content block before creating the
            // synthetic thinking block.
            if (_hasTextContentStarted)
            {
                if (FlushPendingTextCitations() is { } citationError)
                {
                    return Wrap("failed to flush text citations before pending signature", citationError);
                }

                _hasTextContentStarted = false;

                var textStop = new AnthropicStreamEvent
                {
                    Type = "content_block_stop",
                    Index = _contentIndex,
                };

                if (EnqueueEvent(textStop) is { } textStopError)
                {
                    return Wrap("failed to enqueue content_block_stop for text before pending signature", textStopError);
                }

                _contentIndex += 1;
            }

            if (_hasToolContentStarted)
            {
                if (CloseToolBlock() is { } toolError)
                {
                    return Wrap("failed to close tool before pending signature", toolError);
                }
            }

            var thinkingStart = new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = _contentIndex,
                ContentBlock = new AnthropicMessageContentBlock
                {
                    Type = "thinking",
                    Thinking = string.Empty,
                },
            };

            if (EnqueueEvent(thinkingStart) is { } thinkingStartError)
            {
                return Wrap("failed to enqueue thinking content_block_start for pending signature", thinkingStartError);
            }

            var signatureDelta = new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = _contentIndex,
                Delta = new AnthropicStreamDelta
                {
                    Type = "signature_delta",
                    Signature = signature,
                },
            };

            if (EnqueueEvent(signatureDelta) is { } signatureError)
            {
                return Wrap("failed to enqueue signature_delta for pending signature", signatureError);
            }

            var thinkingStop = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(thinkingStop) is { } thinkingStopError)
            {
                return Wrap("failed to enqueue content_block_stop for pending signature", thinkingStopError);
            }

            _contentIndex += 1;

            return null;
        }

        if (_hasThinkingContentStarted)
        {
            _hasThinkingContentStarted = false;

            // Use the pending signature if available, otherwise generate a
            // random one.
            var signature = _pendingSignature ?? AnthropicJson.GenerateSignature();
            _pendingSignature = null;

            var signatureDelta = new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = _contentIndex,
                Delta = new AnthropicStreamDelta
                {
                    Type = "signature_delta",
                    Signature = signature,
                },
            };

            if (EnqueueEvent(signatureDelta) is { } signatureError)
            {
                return Wrap("failed to enqueue signature_delta event", signatureError);
            }

            var stopEvent = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(stopEvent) is { } stopError)
            {
                return Wrap("failed to enqueue content_block_stop event", stopError);
            }

            _contentIndex += 1;
        }

        return null;
    }

    private InvalidOperationException? HandleReasoningContent(
        string reasoningContent,
        Dictionary<string, JsonElement>? metadata)
    {
        var itemId = GetResponsesReasoningItemId(metadata);
        if (itemId.Length > 0)
        {
            RememberReasoningItem(itemId);
            if (!_pendingReasoningContent.TryGetValue(itemId, out var parts))
            {
                parts = [];
                _pendingReasoningContent[itemId] = parts;
            }

            parts.Add(reasoningContent);
            return null;
        }

        // If text content started before thinking, close the text block first.
        if (_hasTextContentStarted)
        {
            if (FlushPendingTextCitations() is { } citationError)
            {
                return Wrap("failed to flush text citations before thinking", citationError);
            }

            _hasTextContentStarted = false;

            var textStop = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(textStop) is { } textStopError)
            {
                return Wrap("failed to enqueue content_block_stop event", textStopError);
            }

            _contentIndex += 1;
        }

        // If a tool block started before thinking, close it first.
        if (_hasToolContentStarted)
        {
            if (CloseToolBlock() is { } toolError)
            {
                return Wrap("failed to close tool block before thinking", toolError);
            }
        }

        if (!_hasThinkingContentStarted)
        {
            _hasThinkingContentStarted = true;

            var thinkingStart = new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = _contentIndex,
                ContentBlock = new AnthropicMessageContentBlock
                {
                    Type = "thinking",
                    Thinking = string.Empty,
                },
            };

            if (EnqueueEvent(thinkingStart) is { } startError)
            {
                return Wrap("failed to enqueue content_block_start event", startError);
            }
        }

        var thinkingDelta = new AnthropicStreamEvent
        {
            Type = "content_block_delta",
            Index = _contentIndex,
            Delta = new AnthropicStreamDelta
            {
                Type = "thinking_delta",
                Thinking = reasoningContent,
            },
        };

        if (EnqueueEvent(thinkingDelta) is { } deltaError)
        {
            return Wrap("failed to enqueue content_block_delta event", deltaError);
        }

        return null;
    }

    private InvalidOperationException? HandleReasoningSignature(
        string reasoningSignature,
        Dictionary<string, JsonElement>? metadata)
    {
        // Responses encrypted content is item-scoped and opaque: when the
        // upstream supplies an item ID, a new ID closes the previous block and a
        // repeated ID replaces the provisional value with the latest value
        // instead of concatenating blobs.
        var itemId = GetResponsesReasoningItemId(metadata);
        if (itemId.Length > 0)
        {
            RememberReasoningItem(itemId);
            _pendingReasoningSignatures[itemId] = reasoningSignature;

            if (ResponsesReasoningItemDone(metadata))
            {
                if (EmitBufferedReasoningItem(itemId) is { } emitError)
                {
                    return Wrap("failed to emit completed reasoning item", emitError);
                }
            }

            return null;
        }

        _pendingSignature = _pendingSignature is null
            ? reasoningSignature
            : _pendingSignature + reasoningSignature;

        return null;
    }

    private InvalidOperationException? HandleRedactedReasoning(string redactedReasoningContent)
    {
        if (CloseThinkingBlock() is { } thinkingError)
        {
            return Wrap("failed to close thinking block", thinkingError);
        }

        if (_hasToolContentStarted)
        {
            if (CloseToolBlock() is { } toolError)
            {
                return Wrap("failed to close tool block before redacted thinking", toolError);
            }
        }

        if (_hasTextContentStarted)
        {
            if (FlushPendingTextCitations() is { } citationError)
            {
                return Wrap("failed to flush text citations", citationError);
            }

            _hasTextContentStarted = false;

            var textStop = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(textStop) is { } stopError)
            {
                return Wrap("failed to enqueue content_block_stop event", stopError);
            }

            _contentIndex += 1;
        }

        // Redacted thinking blocks come complete in content_block_start with
        // their Data field already populated.
        var redactedStart = new AnthropicStreamEvent
        {
            Type = "content_block_start",
            Index = _contentIndex,
            ContentBlock = new AnthropicMessageContentBlock
            {
                Type = "redacted_thinking",
                Data = redactedReasoningContent,
            },
        };

        if (EnqueueEvent(redactedStart) is { } startError)
        {
            return Wrap("failed to enqueue redacted_thinking content_block_start event", startError);
        }

        var redactedStop = new AnthropicStreamEvent
        {
            Type = "content_block_stop",
            Index = _contentIndex,
        };

        if (EnqueueEvent(redactedStop) is { } redactedStopError)
        {
            return Wrap("failed to enqueue redacted_thinking content_block_stop event", redactedStopError);
        }

        _contentIndex += 1;

        return null;
    }

    private InvalidOperationException? HandleTextDelta(string text)
    {
        if (CloseThinkingBlock() is { } thinkingError)
        {
            return Wrap("failed to close thinking block", thinkingError);
        }

        if (_hasToolContentStarted)
        {
            if (CloseToolBlock() is { } toolError)
            {
                return Wrap("failed to close tool block before text", toolError);
            }
        }

        if (!_hasTextContentStarted)
        {
            _hasTextContentStarted = true;

            var textStart = new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = _contentIndex,
                ContentBlock = new AnthropicMessageContentBlock
                {
                    Type = "text",
                    Text = string.Empty,
                },
            };

            if (EnqueueEvent(textStart) is { } startError)
            {
                return Wrap("failed to enqueue content_block_start event", startError);
            }
        }

        var textDelta = new AnthropicStreamEvent
        {
            Type = "content_block_delta",
            Index = _contentIndex,
            Delta = new AnthropicStreamDelta
            {
                Type = "text_delta",
                Text = text,
            },
        };

        if (EnqueueEvent(textDelta) is { } deltaError)
        {
            return Wrap("failed to enqueue content_block_delta event", deltaError);
        }

        return null;
    }

    private InvalidOperationException? HandleToolCalls(List<ToolCall> toolCalls)
    {
        if (CloseThinkingBlock() is { } thinkingError)
        {
            return Wrap("failed to close thinking block", thinkingError);
        }

        if (_hasTextContentStarted)
        {
            if (FlushPendingTextCitations() is { } citationError)
            {
                return Wrap("failed to flush text citations", citationError);
            }

            _hasTextContentStarted = false;

            var textStop = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(textStop) is { } stopError)
            {
                return Wrap("failed to enqueue content_block_stop event", stopError);
            }

            _contentIndex += 1;
        }

        foreach (var deltaToolCall in toolCalls)
        {
            var toolCallIndex = deltaToolCall.Index;

            if (!_toolCalls.TryGetValue(toolCallIndex, out var toolCall))
            {
                // A new tool use block must stop the previous tool use block.
                if (toolCallIndex > 0 && _hasToolContentStarted)
                {
                    if (CloseToolBlock() is { } previousError)
                    {
                        return Wrap("failed to close previous tool block", previousError);
                    }
                }

                _hasToolContentStarted = true;
                _currentToolCallIndex = toolCallIndex;
                _hasCurrentToolCall = true;
                toolCall = new ToolCall
                {
                    Index = toolCallIndex,
                    Id = deltaToolCall.Id,
                    Type = deltaToolCall.Type,
                    Function = new FunctionCall
                    {
                        Name = deltaToolCall.Function.Name,
                        Arguments = string.Empty,
                    },
                    TransformerMetadata = deltaToolCall.TransformerMetadata,
                };
                _toolCalls[toolCallIndex] = toolCall;

                // Restore the original Anthropic block type (tool_use /
                // server_tool_use / mcp_tool_use / ...) and caller when the
                // upstream tagged it via TransformerMetadata.
                var blockType = AnthropicJson.GetAnthropicType(deltaToolCall.TransformerMetadata);
                if (blockType.Length == 0)
                {
                    blockType = "tool_use";
                }

                var toolStart = new AnthropicStreamEvent
                {
                    Type = "content_block_start",
                    Index = _contentIndex,
                    ContentBlock = new AnthropicMessageContentBlock
                    {
                        Type = blockType,
                        Id = deltaToolCall.Id,
                        Name = deltaToolCall.Function.Name,
                        Input = AnthropicJson.RawElement("{}"u8),
                        Caller = AnthropicJson.GetAnthropicCaller(deltaToolCall.TransformerMetadata),
                    },
                };

                if (EnqueueEvent(toolStart) is { } startError)
                {
                    return Wrap("failed to enqueue content_block_start event", startError);
                }

                if (deltaToolCall.Function.Arguments.Length > 0)
                {
                    toolCall.Function.Arguments += deltaToolCall.Function.Arguments;
                    if (AnthropicJson.IsReadToolName(deltaToolCall.Function.Name))
                    {
                        continue;
                    }

                    if (EnqueueInputJsonDelta(deltaToolCall.Function.Arguments) is { } deltaError)
                    {
                        return deltaError;
                    }
                }
            }
            else
            {
                toolCall.Function.Arguments += deltaToolCall.Function.Arguments;
                if (AnthropicJson.IsReadToolName(toolCall.Function.Name))
                {
                    continue;
                }

                if (EnqueueInputJsonDelta(deltaToolCall.Function.Arguments) is { } deltaError)
                {
                    return deltaError;
                }
            }
        }

        return null;
    }

    private InvalidOperationException? EnqueueInputJsonDelta(string partialJson)
    {
        var deltaEvent = new AnthropicStreamEvent
        {
            Type = "content_block_delta",
            Index = _contentIndex,
            Delta = new AnthropicStreamDelta
            {
                Type = "input_json_delta",
                PartialJson = partialJson,
            },
        };

        if (EnqueueEvent(deltaEvent) is { } error)
        {
            return Wrap("failed to enqueue content_block_delta event", error);
        }

        return null;
    }

    private InvalidOperationException? HandleInlineToolResults(List<InlineToolResult> inlineResults)
    {
        if (CloseThinkingBlock() is { } thinkingError)
        {
            return Wrap("failed to close thinking block", thinkingError);
        }

        // Close any open tool_use block before starting a tool_result.
        if (_hasToolContentStarted)
        {
            if (CloseToolBlock() is { } toolError)
            {
                return Wrap("failed to close tool block before tool_result", toolError);
            }
        }

        // Close any open text block before starting a tool_result.
        if (_hasTextContentStarted)
        {
            _hasTextContentStarted = false;

            var textStop = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(textStop) is { } stopError)
            {
                return Wrap("failed to enqueue content_block_stop event", stopError);
            }

            _contentIndex += 1;
        }

        foreach (var inlineResult in inlineResults)
        {
            if (!AnthropicConvertFromLlm.ToolResultBlockFromInline(inlineResult, out var block))
            {
                continue;
            }

            var startEvent = new AnthropicStreamEvent
            {
                Type = "content_block_start",
                Index = _contentIndex,
                ContentBlock = block,
            };

            if (EnqueueEvent(startEvent) is { } startError)
            {
                return Wrap("failed to enqueue *_tool_result content_block_start", startError);
            }

            var stopEvent = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(stopEvent) is { } stopError)
            {
                return Wrap("failed to enqueue *_tool_result content_block_stop", stopError);
            }

            _contentIndex += 1;
        }

        return null;
    }

    private InvalidOperationException? HandleFinishReason(string finishReason)
    {
        _hasFinished = true;

        var contentClosed = false;

        if (CloseThinkingBlock() is { } thinkingError)
        {
            return Wrap("failed to close thinking block", thinkingError);
        }

        if (_lastEventType == "content_block_stop")
        {
            contentClosed = true;
        }

        if (_hasTextContentStarted)
        {
            if (FlushPendingTextCitations() is { } citationError)
            {
                return Wrap("failed to flush text citations", citationError);
            }

            _hasTextContentStarted = false;

            var textStop = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(textStop) is { } stopError)
            {
                return Wrap("failed to enqueue content_block_stop event", stopError);
            }

            _contentIndex += 1;
            contentClosed = true;
        }

        if (_hasToolContentStarted)
        {
            if (CloseToolBlock() is { } toolError)
            {
                return Wrap("failed to close tool block at finish", toolError);
            }

            contentClosed = true;
        }

        if (!contentClosed && !_hasTextContentStarted && !_hasToolContentStarted && !_hasThinkingContentStarted)
        {
            var bareStop = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(bareStop) is { } bareStopError)
            {
                return Wrap("failed to enqueue content_block_stop event", bareStopError);
            }
        }

        // Store the stop reason, but don't generate message_delta yet: wait for
        // the usage chunk to combine them.
        _stopReason = finishReason switch
        {
            "stop" => "end_turn",
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            _ => "end_turn",
        };

        return null;
    }

    private InvalidOperationException? CloseOpenContentBlocks()
    {
        if (CloseThinkingBlock() is { } thinkingError)
        {
            return Wrap("failed to close thinking block", thinkingError);
        }

        if (_hasTextContentStarted)
        {
            if (FlushPendingTextCitations() is { } citationError)
            {
                return Wrap("failed to flush text citations", citationError);
            }

            _hasTextContentStarted = false;

            var textStop = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(textStop) is { } stopError)
            {
                return Wrap("failed to enqueue content_block_stop event", stopError);
            }

            _contentIndex += 1;
        }

        if (_hasToolContentStarted)
        {
            if (CloseToolBlock() is { } toolError)
            {
                return Wrap("failed to close tool block", toolError);
            }
        }

        return null;
    }

    private InvalidOperationException? EnqueueTerminalEvents()
    {
        var usage = _pendingUsage ?? new AnthropicUsage();

        var messageDelta = new AnthropicStreamEvent
        {
            Type = "message_delta",
            Usage = usage,
        };

        if (_stopReason is not null)
        {
            messageDelta.Delta = new AnthropicStreamDelta
            {
                StopReason = _stopReason,
            };
        }

        if (EnqueueEvent(messageDelta) is { } deltaError)
        {
            return Wrap("failed to enqueue message_delta event", deltaError);
        }

        var messageStop = new AnthropicStreamEvent { Type = "message_stop" };

        if (EnqueueEvent(messageStop) is { } stopError)
        {
            return Wrap("failed to enqueue message_stop event", stopError);
        }

        _messageStopped = true;

        return null;
    }

    private Exception? EnqueueEvent(AnthropicStreamEvent streamEvent)
    {
        // Some providers have a bug that generates duplicate content_block_stop
        // events. This check ignores the duplicate for compatibility.
        if (_lastEventType == "content_block_stop" && streamEvent.Type == "content_block_stop")
        {
            return null;
        }

        _lastEventType = streamEvent.Type;

        byte[] eventData;
        try
        {
            eventData = GoJson.Marshal(streamEvent);
        }
        catch (Exception ex)
        {
            return ex;
        }

        _eventQueue.Add(new StreamEvent
        {
            Type = streamEvent.Type,
            Data = eventData,
        });

        return null;
    }

    private static string GetResponsesReasoningItemId(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null
            || !metadata.TryGetValue(ReasoningItemMetadataKey, out var raw)
            || raw.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return raw.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool ResponsesReasoningItemDone(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null
            || !metadata.TryGetValue(ReasoningItemMetadataKey, out var raw)
            || raw.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return raw.TryGetProperty("done", out var done)
               && done.ValueKind is JsonValueKind.True or JsonValueKind.False
               && done.GetBoolean();
    }

    private void RememberReasoningItem(string itemId)
    {
        if (_pendingReasoningContent.ContainsKey(itemId)
            || _pendingReasoningSignatures.ContainsKey(itemId))
        {
            return;
        }

        _pendingReasoningItemOrder.Add(itemId);
    }

    private Exception? EmitBufferedReasoningItem(string itemId)
    {
        var hasContent = _pendingReasoningContent.TryGetValue(itemId, out var content);
        var hasSignature = _pendingReasoningSignatures.TryGetValue(itemId, out var signature);
        if (!hasContent && !hasSignature)
        {
            return null;
        }

        var wasFlushing = _flushingReasoningItems;
        _flushingReasoningItems = true;
        var closeError = CloseThinkingBlock();
        _flushingReasoningItems = wasFlushing;
        if (closeError is not null)
        {
            return closeError;
        }

        if (_hasTextContentStarted)
        {
            if (FlushPendingTextCitations() is { } citationError)
            {
                return citationError;
            }

            _hasTextContentStarted = false;

            var textStop = new AnthropicStreamEvent
            {
                Type = "content_block_stop",
                Index = _contentIndex,
            };

            if (EnqueueEvent(textStop) is { } textStopError)
            {
                return textStopError;
            }

            _contentIndex++;
        }

        if (_hasToolContentStarted)
        {
            if (CloseToolBlock() is { } toolError)
            {
                return toolError;
            }
        }

        var thinkingStart = new AnthropicStreamEvent
        {
            Type = "content_block_start",
            Index = _contentIndex,
            ContentBlock = new AnthropicMessageContentBlock
            {
                Type = "thinking",
                Thinking = string.Empty,
            },
        };

        if (EnqueueEvent(thinkingStart) is { } startError)
        {
            return startError;
        }

        foreach (var item in content ?? [])
        {
            var thinkingDelta = new AnthropicStreamEvent
            {
                Type = "content_block_delta",
                Index = _contentIndex,
                Delta = new AnthropicStreamDelta
                {
                    Type = "thinking_delta",
                    Thinking = item,
                },
            };

            if (EnqueueEvent(thinkingDelta) is { } deltaError)
            {
                return deltaError;
            }
        }

        signature ??= AnthropicJson.GenerateSignature();

        var signatureDelta = new AnthropicStreamEvent
        {
            Type = "content_block_delta",
            Index = _contentIndex,
            Delta = new AnthropicStreamDelta
            {
                Type = "signature_delta",
                Signature = signature,
            },
        };

        if (EnqueueEvent(signatureDelta) is { } signatureError)
        {
            return signatureError;
        }

        var stopEvent = new AnthropicStreamEvent
        {
            Type = "content_block_stop",
            Index = _contentIndex,
        };

        if (EnqueueEvent(stopEvent) is { } stopError)
        {
            return stopError;
        }

        _contentIndex++;
        _pendingReasoningContent.Remove(itemId);
        _pendingReasoningSignatures.Remove(itemId);

        return null;
    }

    private Exception? FlushBufferedReasoningItems()
    {
        foreach (var itemId in _pendingReasoningItemOrder)
        {
            if (EmitBufferedReasoningItem(itemId) is { } error)
            {
                return error;
            }
        }

        _pendingReasoningItemOrder.Clear();
        return null;
    }

    private static InvalidOperationException Wrap(string prefix, Exception error)
        => new InvalidOperationException(prefix + ": " + error.Message, error);
}
