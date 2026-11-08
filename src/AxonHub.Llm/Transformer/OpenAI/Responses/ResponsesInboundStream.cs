using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of responsesInboundStream (inbound_stream.go:37-1388): turns the
/// unified LLM response stream into OpenAI Responses API SSE events.
/// </summary>
/// <remarks>
/// The reference drives its state machine through recursive <c>Next()</c>
/// calls; the port uses an equivalent loop. Failures are carried as
/// <see cref="Err"/> (data, not exceptions), exactly as in the reference.
/// </remarks>
internal sealed class ResponsesInboundStream : IStream<StreamEvent>
{
    private readonly IStream<LlmResponse> _source;

    // State tracking.
    private bool _hasResponseCreated;
    private bool _hasMessageItemStarted;
    private bool _hasReasoningItemStarted;
    private bool _hasReasoningSummaryPart;
    private bool _hasContentPartStarted;
    private bool _hasFinished;
    private bool _responseCompleted;
    private List<Annotation> _pendingAnnotations = [];

    // Response metadata.
    private string _responseId = string.Empty;
    private string _model = string.Empty;
    private long _createdAt;

    // Content tracking.
    private int _outputIndex;
    private int _contentIndex;
    private int _sequenceNumber;
    private string _currentItemId = string.Empty;

    // Content accumulation for items (used for emitting done events).
    private readonly StringBuilder _accumulatedText = new();
    private readonly StringBuilder _accumulatedReasoning = new();
    private readonly StringBuilder _accumulatedReasoningSignature = new();
    private string _currentReasoningSourceId = string.Empty;
    private readonly Dictionary<string, List<string>> _pendingReasoning = new(StringComparer.Ordinal);
    private readonly List<string> _pendingReasoningOrder = [];

    // Tool call tracking.
    private readonly Dictionary<long, ToolCall> _toolCalls = [];
    private readonly Dictionary<long, bool> _toolCallItemStarted = [];
    private readonly Dictionary<long, int> _toolCallOutputIndex = [];

    // Response accumulation using the stream aggregator.
    private Usage? _usage;
    private readonly ResponsesStreamAggregator _aggregator = new();
    private readonly Dictionary<string, JsonElement> _transformerMetadata = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _responseHeaders = [];
    private bool _responseHeadersSent;

    // Event queue.
    private List<StreamEvent> _eventQueue = [];
    private int _queueIndex;
    private Exception? _err;

    // Error event tracking: once an error event has been emitted to the client,
    // Err must return null to avoid a double error emission by the SSE writer.
    private bool _errorEventEmitted;

    public ResponsesInboundStream(IStream<LlmResponse> source)
    {
        _source = source;
    }

    /// <summary>Go: <c>enqueueEvent</c>. Returns the raw marshal error, if any.</summary>
    private Exception? EnqueueEvent(ResponsesStreamEvent ev)
    {
        ev.SequenceNumber = _sequenceNumber;
        _sequenceNumber++;

        byte[] eventData;
        try
        {
            eventData = ResponsesStreamEventMarshal.Marshal(ev);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return ex;
        }

        var streamEvent = new StreamEvent
        {
            Type = ev.Type,
            Data = eventData,
        };
        AttachResponseHeaders(streamEvent);

        _eventQueue.Add(streamEvent);

        // Use the aggregator to accumulate state for response.completed.
        _aggregator.ProcessEvent(ev);
        return null;
    }

    /// <summary>
    /// Port of the <c>fmt.Errorf("failed to enqueue ...: %w", err)</c> call
    /// sites: the prefix is the complete reference prefix, trailing colon and
    /// space included.
    /// </summary>
    private void MustEnqueue(ResponsesStreamEvent ev, string prefix)
    {
        if (EnqueueEvent(ev) is { } err)
        {
            throw GoErrors.Wrap(err, prefix);
        }
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

            // Once a terminal outcome is emitted, do not convert a later source
            // error into a second terminal event.
            if (_responseCompleted)
            {
                return false;
            }

            // Clear the queue and reset the index for new events.
            _eventQueue = [];
            _queueIndex = 0;

            // Try to get the next chunk from the source.
            if (!_source.Next())
            {
                if (_err is null && !_errorEventEmitted && _source.Err is null && _hasFinished)
                {
                    try
                    {
                        EnqueueTerminalResponse();
                    }
                    catch (WrappedException ex)
                    {
                        _err = ex;
                        return false;
                    }

                    continue;
                }

                // Source stream ended - check whether an error event is needed.
                if (_err is null && !_errorEventEmitted && _source.Err is { } sourceErr)
                {
                    // Don't emit an error event for client cancellation.
                    if (IsCancellation(sourceErr))
                    {
                        return false;
                    }

                    Exception? enqueueErr;
                    try
                    {
                        enqueueErr = EmitStreamErrorEvent(sourceErr);
                    }
                    catch (WrappedException ex)
                    {
                        _err = ex;
                        return false;
                    }

                    if (enqueueErr is not null)
                    {
                        _err = GoErrors.Wrap(
                            enqueueErr, "failed to enqueue stream error event: ");
                        return false;
                    }

                    continue;
                }

                return false;
            }

            var chunk = _source.Current;
            if (chunk is null)
            {
                continue; // Try the next chunk.
            }

            // Handle the [DONE] marker.
            if (chunk.Object == "[DONE]")
            {
                continue;
            }

            try
            {
                ProcessChunk(chunk);
            }
            catch (WrappedException ex)
            {
                _err = ex;
                return false;
            }
        }
    }

    /// <summary>Port of the body of <c>Next</c> after the source yielded a chunk.</summary>
    private void ProcessChunk(LlmResponse chunk)
    {
        if (ResponsesStreamMetadata.TryGetTransportHeaders(chunk.TransformerMetadata, out var headers))
        {
            _responseHeaders = headers;
        }

        if (ResponsesStreamMetadata.TryGetRawEvent(chunk.TransformerMetadata, out var raw))
        {
            var metadataEvent = new StreamEvent
            {
                Type = ResponsesStreamEventTypes.ResponseMetadata,
                Data = raw,
            };
            AttachResponseHeaders(metadataEvent);
            _eventQueue.Add(metadataEvent);
            return;
        }

        // Initialize response metadata from the first chunk.
        if (_responseId.Length == 0 && chunk.Id.Length > 0)
        {
            _responseId = chunk.Id;
        }

        if (_model.Length == 0 && chunk.Model.Length > 0)
        {
            _model = chunk.Model;
        }

        // Track createdAt.
        if (_createdAt == 0 && chunk.Created != 0)
        {
            _createdAt = chunk.Created;
        }

        // Track usage.
        if (chunk.Usage is not null)
        {
            _usage = chunk.Usage;
        }

        if (chunk.TransformerMetadata is { Count: > 0 } metadata)
        {
            MergeTransformerMetadata(metadata);
        }

        // Generate the response.created event if this is the first chunk.
        if (!_hasResponseCreated)
        {
            _hasResponseCreated = true;

            var response = new ResponsesResponse
            {
                Object = "response",
                Id = _responseId,
                Model = _model,
                CreatedAt = _createdAt,
                Status = "in_progress",
                Output = [],
            };

            if (_usage is not null)
            {
                response.Usage = ResponsesUsage.ConvertLlmUsageToResponsesUsage(_usage);
            }

            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.ResponseCreated,
                    Response = response,
                },
                "failed to enqueue response.created event: ");

            // Also emit response.in_progress.
            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.ResponseInProgress,
                    Response = response,
                },
                "failed to enqueue response.in_progress event: ");
        }

        // Process choices.
        if (chunk.Choices.Count > 0)
        {
            var choice = chunk.Choices[0];

            // Handle reasoning content (thinking) delta.
            if (choice.Delta is { ReasoningContent: { Length: > 0 } reasoningContent })
            {
                HandleReasoningContent(reasoningContent, chunk.TransformerMetadata);
            }

            // Handle encrypted reasoning content delta (stored in
            // ReasoningSignature).
            if (choice.Delta is { ReasoningSignature: { Length: > 0 } } delta)
            {
                HandleReasoningSignature(delta, chunk.TransformerMetadata);
            }

            if (choice.Message?.Annotations is { Count: > 0 } messageAnnotations)
            {
                _pendingAnnotations.AddRange(messageAnnotations);
            }

            if (choice.Delta?.Annotations is { Count: > 0 } deltaAnnotations)
            {
                _pendingAnnotations.AddRange(deltaAnnotations);
            }

            // Handle text content delta.
            if (choice.Delta?.Content?.Content is { Length: > 0 } content)
            {
                HandleTextContent(content);
            }

            if (choice.Delta?.Content?.MultipleContent is { Count: > 0 } multipleContent)
            {
                HandleCompactionContent(multipleContent);
            }

            // Handle tool calls.
            if (choice.Delta?.ToolCalls is { Count: > 0 } toolCalls)
            {
                HandleToolCalls(toolCalls);
            }

            // Handle finish reason.
            if (choice.FinishReason is { } finishReason && !_hasFinished)
            {
                _hasFinished = true;

                // Map the finish_reason to the final Responses status and event
                // type.
                switch (finishReason)
                {
                    case "length":
                        _aggregator.Status = "incomplete";
                        _aggregator.IncompleteDetails = new ResponsesResponseIncompleteDetails
                        {
                            Reason = "max_output_tokens",
                        };
                        break;
                    case "content_filter":
                        _aggregator.Status = "incomplete";
                        _aggregator.IncompleteDetails = new ResponsesResponseIncompleteDetails
                        {
                            Reason = "content_filter",
                        };
                        break;
                    case "error":
                        _aggregator.Status = "failed";
                        break;
                    case "cancelled":
                    case "canceled":
                        _aggregator.Status = "cancelled";
                        break;
                }

                FlushPendingReasoning();

                // Close any open content parts.
                CloseCurrentContentPart();

                // Close any open output items.
                CloseCurrentOutputItem();
            }
        }

        // Usage follows the finish_reason; emit the final outcome once both
        // arrive. The terminal event must wait for the choices-less usage-only
        // chunk that carries the real counts. Some providers (e.g. ModelScope)
        // attach a zeroed usage object to every chunk, including the one
        // carrying finish_reason; finalizing there would emit the placeholder
        // zeros and drop the real counts arriving afterwards.
        if (chunk.Usage is not null && chunk.Choices.Count == 0 && _hasFinished
            && !_responseCompleted)
        {
            EnqueueTerminalResponse();
        }
    }

    /// <summary>
    /// Port of <c>enqueueTerminalResponse</c>: serves both the final usage
    /// chunk and a clean stream end.
    /// </summary>
    private void EnqueueTerminalResponse()
    {
        if (_aggregator.Status.Length == 0 || _aggregator.Status == "in_progress")
        {
            _aggregator.Status = "completed";
        }

        var response = _aggregator.BuildResponse();
        if (_usage is not null)
        {
            response.Usage = ResponsesUsage.ConvertLlmUsageToResponsesUsage(_usage);
        }

        if (ResponsesInboundConvert.GetResponseWebSearchCallsFromMetadata(_transformerMetadata)
            is { Count: > 0 } calls)
        {
            // Go: append(append([]Item(nil), calls...), response.Output...).
            var existingOutput = response.Output ?? [];
            var output = new List<ResponsesItem>(calls.Count + existingOutput.Count);
            output.AddRange(calls);
            output.AddRange(existingOutput);
            response.Output = output;
        }

        if (_transformerMetadata.TryGetValue(
                ResponsesKeys.TerminalDetailsTransformerMetadataKey, out var raw))
        {
            // Metadata may have crossed a JSON serialization boundary.
            byte[] data;
            try
            {
                data = GoJson.Marshal(raw);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw GoErrors.Wrap(ex, "failed to encode terminal details: ");
            }

            ResponsesTerminalDetails? details;
            try
            {
                details = LlmJson.Deserialize<ResponsesTerminalDetails>(data);
            }
            catch (JsonException ex)
            {
                throw GoErrors.Wrap(ex, "failed to decode terminal details: ");
            }

            if (details is not null)
            {
                if (details.Error is not null)
                {
                    response.Error = details.Error;
                }

                if (details.IncompleteDetails is not null)
                {
                    response.IncompleteDetails = details.IncompleteDetails;
                }
            }
        }

        var eventType = ResponsesStreamEventTypes.ResponseCompleted;
        switch (_aggregator.Status)
        {
            case "failed":
                eventType = ResponsesStreamEventTypes.ResponseFailed;
                break;
            case "incomplete":
                eventType = ResponsesStreamEventTypes.ResponseIncomplete;
                break;
        }

        // Cancellation keeps the existing response.completed compatibility
        // format.
        MustEnqueue(
            new ResponsesStreamEvent { Type = eventType, Response = response },
            "failed to enqueue terminal response: ");
        _responseCompleted = true;
    }

    private void MergeTransformerMetadata(Dictionary<string, JsonElement> metadata)
    {
        if (metadata.Count == 0)
        {
            return;
        }

        if (metadata.TryGetValue(
                ResponsesKeys.TerminalDetailsTransformerMetadataKey, out var details))
        {
            _transformerMetadata[ResponsesKeys.TerminalDetailsTransformerMetadataKey] = details;
        }

        if (ResponsesInboundConvert.GetResponseWebSearchCallsFromMetadata(metadata)
            is { Count: > 0 } calls)
        {
            var existingCalls =
                ResponsesInboundConvert.GetResponseWebSearchCallsFromMetadata(_transformerMetadata)
                ?? [];
            existingCalls.AddRange(calls);
            _transformerMetadata[ResponsesKeys.WebSearchCallsTransformerMetadataKey] =
                ResponsesConvertUtil.ToElement(existingCalls);
        }
    }

    private void AttachResponseHeaders(StreamEvent ev)
    {
        if (_responseHeadersSent || _responseHeaders.Count == 0)
        {
            return;
        }

        ev.Headers = ResponsesStreamMetadata.CloneHeaders(_responseHeaders);
        _responseHeadersSent = true;
    }

    private void HandleReasoningContent(
        string content, Dictionary<string, JsonElement>? metadata)
    {
        var sourceId = ResponsesStreamMetadata.TryGetReasoningItemMetadata(metadata, out var item)
            ? item.Id!
            : string.Empty;

        if (sourceId.Length > 0)
        {
            if (!_pendingReasoning.TryGetValue(sourceId, out var pending))
            {
                _pendingReasoningOrder.Add(sourceId);
                pending = [];
                _pendingReasoning[sourceId] = pending;
            }

            pending.Add(content);
            return;
        }

        EmitReasoningContent(content, sourceId);
    }

    private void EmitReasoningContent(string content, string sourceId)
    {
        EnsureReasoningItemStarted(sourceId);

        // Start the reasoning summary part only when we actually have summary
        // text.
        if (!_hasReasoningSummaryPart)
        {
            _hasReasoningSummaryPart = true;

            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.ReasoningSummaryPartAdded,
                    ItemId = _currentItemId,
                    OutputIndex = _outputIndex,
                    SummaryIndex = 0,
                    Part = new ResponsesStreamEventPart { Type = "summary_text" },
                },
                "failed to enqueue reasoning_summary_part.added event: ");
        }

        // Accumulate reasoning content.
        _accumulatedReasoning.Append(content);

        // Emit reasoning_summary_text.delta.
        MustEnqueue(
            new ResponsesStreamEvent
            {
                Type = ResponsesStreamEventTypes.ReasoningSummaryTextDelta,
                ItemId = _currentItemId,
                OutputIndex = _outputIndex,
                SummaryIndex = 0,
                Delta = content,
            },
            "failed to enqueue reasoning_summary_text.delta event: ");
    }

    private void HandleReasoningSignature(Message delta, Dictionary<string, JsonElement>? metadata)
    {
        var sourceId = delta.Id ?? string.Empty;

        var itemScoped =
            ResponsesStreamMetadata.TryGetReasoningItemMetadata(metadata, out var itemMetadata);
        if (itemScoped)
        {
            sourceId = itemMetadata.Id!;
        }

        if (sourceId.Length > 0 && _pendingReasoning.TryGetValue(sourceId, out var contents))
        {
            foreach (var content in contents)
            {
                EmitReasoningContent(content, sourceId);
            }

            _pendingReasoning.Remove(sourceId);
            RemovePendingReasoningId(sourceId);
        }

        EnsureReasoningItemStarted(sourceId);

        if (itemScoped)
        {
            // Responses encrypted_content is an opaque item-level value.
            // Repeated values for the same item are provisional/final
            // replacements, not deltas.
            _accumulatedReasoningSignature.Clear();
        }

        _accumulatedReasoningSignature.Append(delta.ReasoningSignature);

        if (itemScoped && itemMetadata.Done)
        {
            CloseReasoningItem();
            return;
        }

        // OpenAI Responses encrypted_content is emitted as an opaque item-level
        // blob, not as a text-style delta. Close the item immediately so
        // consecutive reasoning blobs from separate upstream items are not
        // concatenated.
        if (sourceId.Length == 0 && delta.ReasoningContent is null)
        {
            CloseReasoningItem();
        }
    }

    private void RemovePendingReasoningId(string sourceId)
    {
        for (var i = 0; i < _pendingReasoningOrder.Count; i++)
        {
            if (_pendingReasoningOrder[i] == sourceId)
            {
                _pendingReasoningOrder.RemoveAt(i);
                return;
            }
        }
    }

    private void FlushPendingReasoning()
    {
        while (_pendingReasoningOrder.Count > 0)
        {
            var sourceId = _pendingReasoningOrder[0];
            if (!_pendingReasoning.TryGetValue(sourceId, out var contents))
            {
                RemovePendingReasoningId(sourceId);
                continue;
            }

            foreach (var content in contents)
            {
                EmitReasoningContent(content, sourceId);
            }

            _pendingReasoning.Remove(sourceId);
            RemovePendingReasoningId(sourceId);
            CloseReasoningItem();
        }
    }

    private void EnsureReasoningItemStarted(string sourceId)
    {
        // Start the reasoning output item if not started.
        if (_hasReasoningItemStarted)
        {
            if (sourceId.Length == 0
                || _currentReasoningSourceId.Length == 0
                || _currentReasoningSourceId == sourceId)
            {
                return;
            }

            CloseReasoningItem();
        }

        // Close any previous output item.
        CloseCurrentOutputItem();

        _hasReasoningItemStarted = true;
        _hasReasoningSummaryPart = false;
        _currentReasoningSourceId = sourceId;

        _currentItemId = sourceId.Length > 0
            ? sourceId
            : ResponsesInboundConvert.GenerateItemId();
        var item = new ResponsesItem
        {
            Id = _currentItemId,
            Type = "reasoning",
            Status = "in_progress",
            Summary = [],
        };

        MustEnqueue(
            new ResponsesStreamEvent
            {
                Type = ResponsesStreamEventTypes.OutputItemAdded,
                OutputIndex = _outputIndex,
                Item = item,
            },
            "failed to enqueue output_item.added event: ");
    }

    private void HandleTextContent(string content)
    {
        FlushPendingReasoning();

        // Close the reasoning item if it was started.
        if (_hasReasoningItemStarted)
        {
            CloseReasoningItem();
        }

        // Start the message output item if not started.
        if (!_hasMessageItemStarted)
        {
            _hasMessageItemStarted = true;

            _currentItemId = ResponsesInboundConvert.GenerateItemId();

            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.OutputItemAdded,
                    OutputIndex = _outputIndex,
                    Item = new ResponsesItem
                    {
                        Id = _currentItemId,
                        Type = "message",
                        Status = "in_progress",
                        Role = "assistant",
                        Content = new ResponsesInput { Items = [] },
                    },
                },
                "failed to enqueue output_item.added event: ");
        }

        // Start the content part if not started.
        if (!_hasContentPartStarted)
        {
            _hasContentPartStarted = true;

            var (textPartItems, _) = ResponsesInboundConvert.AttachAnnotationsToFirstTextItem(
                [new ResponsesItem { Type = "output_text", Annotations = [] }],
                _pendingAnnotations);

            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.ContentPartAdded,
                    ItemId = _currentItemId,
                    OutputIndex = _outputIndex,
                    ContentIndex = _contentIndex,
                    Part = new ResponsesStreamEventPart
                    {
                        Type = "output_text",
                        Text = string.Empty,
                        Annotations = textPartItems[0].Annotations,
                    },
                },
                "failed to enqueue content_part.added event: ");

            // Keep pendingAnnotations until output_item.done so the final
            // message item preserves them.
        }

        // Accumulate text content.
        _accumulatedText.Append(content);

        // Emit output_text.delta.
        MustEnqueue(
            new ResponsesStreamEvent
            {
                Type = ResponsesStreamEventTypes.OutputTextDelta,
                ItemId = _currentItemId,
                OutputIndex = _outputIndex,
                ContentIndex = _contentIndex,
                Delta = content,
            },
            "failed to enqueue output_text.delta event: ");
    }

    private void HandleCompactionContent(List<MessageContentPart> parts)
    {
        foreach (var part in parts)
        {
            if (part.Type is not ("compaction" or "compaction_summary") || part.Compact is null)
            {
                continue;
            }

            CloseCurrentOutputItem();

            var item = ResponsesCompactConvert.CompactionItemFromPart(part, part.Type);
            if (item.Id.Length == 0)
            {
                item.Id = ResponsesInboundConvert.GenerateItemId();
            }

            foreach (var eventType in new[]
                     {
                         ResponsesStreamEventTypes.OutputItemAdded,
                         ResponsesStreamEventTypes.OutputItemDone,
                     })
            {
                MustEnqueue(
                    new ResponsesStreamEvent
                    {
                        Type = eventType,
                        OutputIndex = _outputIndex,
                        Item = item,
                    },
                    "failed to enqueue compaction " + eventType + " event: ");
            }

            _outputIndex++;
        }
    }

    private void HandleToolCalls(List<ToolCall> toolCalls)
    {
        FlushPendingReasoning();

        // Close the message item if it was started.
        if (_hasMessageItemStarted)
        {
            CloseMessageItem();
        }

        // Close the reasoning item if it was started.
        if (_hasReasoningItemStarted)
        {
            CloseReasoningItem();
        }

        foreach (var tc in toolCalls)
        {
            var toolCallIndex = tc.Index;

            // Initialize tool call tracking if needed.
            if (!_toolCalls.ContainsKey(toolCallIndex))
            {
                InitToolCall(tc);
            }

            // Process the delta based on the tool type.
            if (tc.ResponseCustomToolCall is not null)
            {
                HandleCustomToolCallDelta(tc);
            }
            else
            {
                HandleFunctionCallDelta(tc);
            }
        }
    }

    private void InitToolCall(ToolCall tc)
    {
        var toolCallIndex = tc.Index;

        CloseCurrentContentPart();
        CloseCurrentOutputItem();

        _toolCalls[toolCallIndex] = new ToolCall
        {
            Index = toolCallIndex,
            Id = tc.Id,
            Type = tc.Type,
            ResponseCustomToolCall = tc.ResponseCustomToolCall,
            Function = new FunctionCall
            {
                Name = tc.Function.Name,
                Namespace = tc.Function.Namespace,
                Arguments = string.Empty,
            },
        };

        // A Responses function_call must include its name in output_item.added
        // for clients to route it. Some upstreams provide that identity only in
        // a later arguments delta or done event, so retain the call until it is
        // known.
        if (tc.ResponseCustomToolCall is null && tc.Function.Name.Length == 0)
        {
            return;
        }

        StartToolCallItem(toolCallIndex);
    }

    private void StartToolCallItem(long toolCallIndex)
    {
        if (_toolCallItemStarted.TryGetValue(toolCallIndex, out var started) && started)
        {
            return;
        }

        var tc = _toolCalls[toolCallIndex];

        var itemId = tc.Id ?? string.Empty;
        if (itemId.Length == 0)
        {
            itemId = ResponsesInboundConvert.GenerateItemId();
        }

        if (tc.ResponseCustomToolCall is { } custom)
        {
            var item = new ResponsesItem
            {
                Id = itemId,
                Type = "custom_tool_call",
                Status = "in_progress",
                CallId = custom.CallId,
                Name = custom.Name,
                Input = string.Empty,
            };

            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.OutputItemAdded,
                    OutputIndex = _outputIndex,
                    Item = item,
                },
                "failed to enqueue output_item.added event: ");
        }
        else
        {
            var item = new ResponsesItem
            {
                Id = itemId,
                Type = "function_call",
                Status = "in_progress",
                CallId = tc.Id ?? string.Empty,
                Name = tc.Function.Name,
                Namespace = tc.Function.Namespace ?? string.Empty,
            };

            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.OutputItemAdded,
                    OutputIndex = _outputIndex,
                    Item = item,
                },
                "failed to enqueue output_item.added event: ");
        }

        _toolCallItemStarted[toolCallIndex] = true;
        _toolCallOutputIndex[toolCallIndex] = _outputIndex;
        _currentItemId = itemId;
        _outputIndex++;
    }

    private void HandleFunctionCallDelta(ToolCall tc)
    {
        var toolCallIndex = tc.Index;
        var storedToolCall = _toolCalls[toolCallIndex];
        if (!string.IsNullOrEmpty(tc.Id))
        {
            storedToolCall.Id = tc.Id;
        }

        if (!string.IsNullOrEmpty(tc.Type))
        {
            storedToolCall.Type = tc.Type;
        }

        if (tc.Function.Name.Length > 0)
        {
            storedToolCall.Function.Name = tc.Function.Name;
        }

        if (!string.IsNullOrEmpty(tc.Function.Namespace))
        {
            storedToolCall.Function.Namespace = tc.Function.Namespace;
        }

        storedToolCall.Function.Arguments += tc.Function.Arguments;

        var argumentsToEmit = tc.Function.Arguments;
        if (!(_toolCallItemStarted.TryGetValue(toolCallIndex, out var started) && started))
        {
            if (storedToolCall.Function.Name.Length == 0)
            {
                return;
            }

            StartToolCallItem(toolCallIndex);

            // Arguments received before the identity became available have not
            // been emitted. Replay the complete buffered payload after
            // output_item.added.
            argumentsToEmit = storedToolCall.Function.Arguments;
        }

        if (argumentsToEmit.Length > 0)
        {
            var itemId = storedToolCall.Id ?? string.Empty;
            if (itemId.Length == 0)
            {
                itemId = _currentItemId;
            }

            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.FunctionCallArgumentsDelta,
                    ItemId = itemId,
                    OutputIndex = _toolCallOutputIndex[toolCallIndex],
                    Delta = argumentsToEmit,
                },
                "failed to enqueue function_call_arguments.delta event: ");
        }
    }

    private void HandleCustomToolCallDelta(ToolCall tc)
    {
        var toolCallIndex = tc.Index;
        var storedToolCall = _toolCalls[toolCallIndex];
        storedToolCall.ResponseCustomToolCall!.Input += tc.ResponseCustomToolCall!.Input;

        if (tc.ResponseCustomToolCall.Input.Length > 0)
        {
            var itemId = storedToolCall.Id ?? string.Empty;
            if (itemId.Length == 0)
            {
                itemId = _currentItemId;
            }

            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.CustomToolCallInputDelta,
                    ItemId = itemId,
                    OutputIndex = _toolCallOutputIndex[toolCallIndex],
                    Delta = tc.ResponseCustomToolCall.Input,
                },
                "failed to enqueue custom_tool_call_input.delta event: ");
        }
    }

    private void CloseReasoningItem()
    {
        if (!_hasReasoningItemStarted)
        {
            return;
        }

        _hasReasoningItemStarted = false;
        var fullReasoning = _accumulatedReasoning.ToString();
        var hadSummaryPart = _hasReasoningSummaryPart;

        // Emit reasoning summary done events only if we started the summary
        // part.
        if (hadSummaryPart)
        {
            // Emit reasoning_summary_text.done with the accumulated text.
            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.ReasoningSummaryTextDone,
                    ItemId = _currentItemId,
                    OutputIndex = _outputIndex,
                    SummaryIndex = 0,
                    Text = ResponsesConvertUtil.NullIfEmpty(fullReasoning),
                },
                "failed to enqueue reasoning_summary_text.done event: ");

            // Emit reasoning_summary_part.done.
            MustEnqueue(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.ReasoningSummaryPartDone,
                    ItemId = _currentItemId,
                    OutputIndex = _outputIndex,
                    SummaryIndex = 0,
                    Part = new ResponsesStreamEventPart
                    {
                        Type = "summary_text",
                        Text = fullReasoning,
                    },
                },
                "failed to enqueue reasoning_summary_part.done event: ");
        }

        _hasReasoningSummaryPart = false;

        // Emit output_item.done with the complete reasoning item.
        var encryptedContent = _accumulatedReasoningSignature.Length > 0
            ? _accumulatedReasoningSignature.ToString()
            : null;

        List<ResponsesReasoningSummary> summary = hadSummaryPart
            ? [new ResponsesReasoningSummary { Type = "summary_text", Text = fullReasoning }]
            : [];

        var item = new ResponsesItem
        {
            Id = _currentItemId,
            Type = "reasoning",
            Summary = summary,
            EncryptedContent = encryptedContent,
        };

        MustEnqueue(
            new ResponsesStreamEvent
            {
                Type = ResponsesStreamEventTypes.OutputItemDone,
                OutputIndex = _outputIndex,
                Item = item,
            },
            "failed to enqueue output_item.done event: ");

        _outputIndex++;
        _accumulatedReasoning.Clear();
        _accumulatedReasoningSignature.Clear();
        _currentReasoningSourceId = string.Empty;
    }

    private void CloseMessageItem()
    {
        if (!_hasMessageItemStarted)
        {
            return;
        }

        _hasMessageItemStarted = false;
        var fullText = _accumulatedText.ToString();

        // Close the content part first.
        CloseCurrentContentPart();

        // Emit output_item.done with the complete message content.
        var contentItems = new List<ResponsesItem>
        {
            new() { Type = "output_text", Text = fullText, Annotations = [] },
        };
        (contentItems, _) = ResponsesInboundConvert.AttachAnnotationsToFirstTextItem(
            contentItems, _pendingAnnotations);

        var item = new ResponsesItem
        {
            Id = _currentItemId,
            Type = "message",
            Status = "completed",
            Role = "assistant",
            Content = new ResponsesInput { Items = contentItems },
        };
        _pendingAnnotations = [];

        MustEnqueue(
            new ResponsesStreamEvent
            {
                Type = ResponsesStreamEventTypes.OutputItemDone,
                OutputIndex = _outputIndex,
                Item = item,
            },
            "failed to enqueue output_item.done event: ");

        _outputIndex++;
        _contentIndex = 0;
        _accumulatedText.Clear();
    }

    private void CloseCurrentContentPart()
    {
        if (!_hasContentPartStarted)
        {
            return;
        }

        _hasContentPartStarted = false;
        var fullText = _accumulatedText.ToString();

        // Emit output_text.done with the accumulated text.
        MustEnqueue(
            new ResponsesStreamEvent
            {
                Type = ResponsesStreamEventTypes.OutputTextDone,
                ItemId = _currentItemId,
                OutputIndex = _outputIndex,
                ContentIndex = _contentIndex,
                Text = ResponsesConvertUtil.NullIfEmpty(fullText),
            },
            "failed to enqueue output_text.done event: ");

        // Emit content_part.done with the full text.
        var (contentPartItems, _) = ResponsesInboundConvert.AttachAnnotationsToFirstTextItem(
            [new ResponsesItem { Type = "output_text", Text = fullText, Annotations = [] }],
            _pendingAnnotations);

        MustEnqueue(
            new ResponsesStreamEvent
            {
                Type = ResponsesStreamEventTypes.ContentPartDone,
                ItemId = _currentItemId,
                OutputIndex = _outputIndex,
                ContentIndex = _contentIndex,
                Part = new ResponsesStreamEventPart
                {
                    Type = "output_text",
                    Text = fullText,
                    Annotations = contentPartItems[0].Annotations,
                },
            },
            "failed to enqueue content_part.done event: ");
    }

    private void CloseCurrentOutputItem()
    {
        // Close the message item if open.
        if (_hasMessageItemStarted)
        {
            CloseMessageItem();
        }

        // Close the reasoning item if open.
        if (_hasReasoningItemStarted)
        {
            CloseReasoningItem();
        }

        // Close any open tool call items. Go iterates a map (random order); the
        // port keeps insertion order, which fixtures require and which is
        // deterministic for clients (difference C068).
        foreach (var (idx, tc) in _toolCalls)
        {
            if (!(_toolCallItemStarted.TryGetValue(idx, out var started) && started))
            {
                continue;
            }

            var itemId = tc.Id ?? string.Empty;
            if (itemId.Length == 0)
            {
                itemId = _currentItemId;
            }

            if (tc.ResponseCustomToolCall is { } custom)
            {
                // Custom tool call - emit custom_tool_call_input.done then
                // output_item.done.
                var fullInput = custom.Input;

                MustEnqueue(
                    new ResponsesStreamEvent
                    {
                        Type = ResponsesStreamEventTypes.CustomToolCallInputDone,
                        ItemId = itemId,
                        OutputIndex = _toolCallOutputIndex[idx],
                        Input = ResponsesConvertUtil.NullIfEmpty(fullInput),
                    },
                    "failed to enqueue custom_tool_call_input.done event: ");

                var item = new ResponsesItem
                {
                    Id = itemId,
                    Type = "custom_tool_call",
                    Status = "completed",
                    CallId = custom.CallId,
                    Name = custom.Name,
                    Input = fullInput,
                };

                MustEnqueue(
                    new ResponsesStreamEvent
                    {
                        Type = ResponsesStreamEventTypes.OutputItemDone,
                        OutputIndex = _toolCallOutputIndex[idx],
                        Item = item,
                    },
                    "failed to enqueue output_item.done event: ");
            }
            else
            {
                // Function call - emit function_call_arguments.done then
                // output_item.done.
                MustEnqueue(
                    new ResponsesStreamEvent
                    {
                        Type = ResponsesStreamEventTypes.FunctionCallArgumentsDone,
                        ItemId = itemId,
                        OutputIndex = _toolCallOutputIndex[idx],
                        CallId = ResponsesConvertUtil.NullIfEmpty(tc.Id),
                        Name = ResponsesConvertUtil.NullIfEmpty(tc.Function.Name),
                        Namespace = ResponsesConvertUtil.NullIfEmpty(tc.Function.Namespace),
                        Arguments = ResponsesConvertUtil.NullIfEmpty(tc.Function.Arguments),
                    },
                    "failed to enqueue function_call_arguments.done event: ");

                var item = new ResponsesItem
                {
                    Id = itemId,
                    Type = "function_call",
                    Status = "completed",
                    CallId = tc.Id ?? string.Empty,
                    Name = tc.Function.Name,
                    Namespace = tc.Function.Namespace ?? string.Empty,
                    Arguments = tc.Function.Arguments,
                };

                MustEnqueue(
                    new ResponsesStreamEvent
                    {
                        Type = ResponsesStreamEventTypes.OutputItemDone,
                        OutputIndex = _toolCallOutputIndex[idx],
                        Item = item,
                    },
                    "failed to enqueue output_item.done event: ");
            }

            _toolCallItemStarted[idx] = false;
        }
    }

    /// <summary>
    /// Port of <c>emitStreamErrorEvent</c>. Returns the raw enqueue failure so
    /// the caller can add the reference's outer wrap.
    /// </summary>
    private Exception? EmitStreamErrorEvent(Exception err)
    {
        var (code, message) = ClassifyStreamError(err);

        Exception? enqueueErr;
        if (_hasResponseCreated)
        {
            var response = BuildFailedResponse(code, message);
            enqueueErr = EnqueueEvent(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.ResponseFailed,
                    Response = response,
                });
        }
        else
        {
            enqueueErr = EnqueueEvent(
                new ResponsesStreamEvent
                {
                    Type = ResponsesStreamEventTypes.Error,
                    Code = code,
                    Message = message,
                });
        }

        if (enqueueErr is not null)
        {
            return enqueueErr;
        }

        _errorEventEmitted = true;
        return null;
    }

    /// <summary>Port of <c>classifyStreamError</c>.</summary>
    internal static (string Code, string Message) ClassifyStreamError(Exception err)
    {
        if (HasCause<EndOfStreamException>(err))
        {
            return ("upstream_eof", "upstream connection closed unexpectedly");
        }

        // .NET reports both a client cancellation and an HTTP timeout as
        // OperationCanceledException; an HttpClient timeout wraps a
        // TimeoutException, which is the analogue of context.DeadlineExceeded.
        if (HasCause<TimeoutException>(err))
        {
            return ("timeout", "request timeout");
        }

        if (HasCause<OperationCanceledException>(err))
        {
            return ("client_cancel", "client disconnected");
        }

        if (GoErrors.As<HttpClientError>(err) is { } httpErr)
        {
            var message = Encoding.UTF8.GetString(httpErr.Body);
            if (message.Length == 0)
            {
                message = httpErr.Status;
            }

            return ("api_error", message);
        }

        if (GoErrors.Is(err, LlmErrors.StreamIncomplete))
        {
            return ("incomplete_stream", "stream ended without terminal event");
        }

        return ("stream_error", err.Message);
    }

    private static bool HasCause<T>(Exception err)
        where T : Exception
        => GoErrors.As<T>(err) is not null;

    /// <summary>
    /// Go treats <c>context.Canceled</c> and <c>context.DeadlineExceeded</c>
    /// as silent stops (inbound_stream.go:155-162).
    /// </summary>
    private static bool IsCancellation(Exception err)
        => HasCause<OperationCanceledException>(err) || HasCause<TimeoutException>(err);

    private ResponsesResponse BuildFailedResponse(string code, string message)
    {
        var response = new ResponsesResponse
        {
            Object = "response",
            Id = _responseId,
            Model = _model,
            CreatedAt = _createdAt,
            Status = "failed",
            Output = [],
            Error = new ResponsesError
            {
                Type = "server_error",
                Code = code,
                Message = message,
            },
        };

        response.Output = _aggregator.BuildResponse().Output;
        return response;
    }

    public StreamEvent? Current
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
            // If an error event has already been emitted to the client, return
            // null to avoid a double error emission by the SSE writer.
            if (_errorEventEmitted)
            {
                return null;
            }

            if (_err is not null)
            {
                return _err;
            }

            // A source error after a terminal response must not become a second
            // outcome.
            if (_responseCompleted)
            {
                return null;
            }

            return _source.Err;
        }
    }

    public void Dispose() => _source.Dispose();
}
