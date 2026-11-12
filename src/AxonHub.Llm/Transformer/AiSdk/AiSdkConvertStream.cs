using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.AiSdk;

/// <summary>
/// Port of aisdk.aiSDKConvertStream (llm/transformer/aisdk/convert_stream.go):
/// the stateful translation from unified response chunks to AI SDK data stream
/// protocol events. Events are queued per source chunk and yielded one at a
/// time, matching the reference's <c>Next</c>/<c>Current</c> pair.
/// </summary>
public sealed class AiSdkConvertStream : IStream<StreamEvent>
{
    private readonly IStream<LlmResponse> _source;
    private readonly List<StreamEvent> _queue = [];
    private readonly Dictionary<string, ToolCall> _activeToolCalls = new(StringComparer.Ordinal);
    private bool _hasStarted;
    private bool _hasFinished;
    private string _messageId = string.Empty;
    private int _queueIndex;
    private Exception? _err;
    private bool _hasTextContentStarted;
    private bool _hasReasoningContentStarted;
    private bool _hasToolContentStarted;
    private string _currentTextId = string.Empty;
    private string _currentReasoningId = string.Empty;

    public AiSdkConvertStream(IStream<LlmResponse> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public StreamEvent? Current { get; private set; }

    public Exception? Err => _err ?? _source.Err;

    public bool Next()
    {
        if (_err is not null)
        {
            return false;
        }

        while (true)
        {
            if (_queueIndex < _queue.Count)
            {
                Current = _queue[_queueIndex];
                _queueIndex++;
                return true;
            }

            _queue.Clear();
            _queueIndex = 0;

            if (!_source.Next())
            {
                return false;
            }

            var chunk = _source.Current;
            if (chunk is null)
            {
                continue;
            }

            if (chunk.Object == "[DONE]")
            {
                continue;
            }

            ProcessChunk(chunk);
            if (_err is not null)
            {
                return false;
            }
        }
    }

    public void Dispose() => _source.Dispose();

    private void ProcessChunk(LlmResponse chunk)
    {
        if (_messageId.Length == 0 && chunk.Id.Length > 0)
        {
            _messageId = chunk.Id;
        }

        if (!_hasStarted)
        {
            _hasStarted = true;

            if (Enqueue(new AiSdkStreamEvent
                {
                    Type = "start",
                    MessageId = GoStr(_messageId),
                }) is { } startError)
            {
                _err = new WrappedException(
                    "failed to enqueue start event: " + startError.Message,
                    startError);
                return;
            }
        }

        if (chunk.Choices.Count == 0)
        {
            return;
        }

        var choice = chunk.Choices[0];
        var delta = choice.Delta;

        var reasoningDelta = delta?.ReasoningContent;
        if (!string.IsNullOrEmpty(reasoningDelta))
        {
            if (_hasToolContentStarted)
            {
                if (EndToolContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (_hasTextContentStarted)
            {
                if (EndTextContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (!_hasReasoningContentStarted)
            {
                if (StartReasoningContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (Enqueue(new AiSdkStreamEvent
                {
                    Type = "reasoning-delta",
                    Id = _currentReasoningId,
                    Delta = reasoningDelta,
                }) is { } deltaError)
            {
                _err = new WrappedException(
                    "failed to enqueue reasoning-delta event: " + deltaError.Message,
                    deltaError);
                return;
            }
        }

        var textDelta = delta?.Content?.Content;
        if (!string.IsNullOrEmpty(textDelta))
        {
            if (_hasReasoningContentStarted)
            {
                if (EndReasoningContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (_hasToolContentStarted)
            {
                if (EndToolContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (!_hasTextContentStarted)
            {
                if (StartTextContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (Enqueue(new AiSdkStreamEvent
                {
                    Type = "text-delta",
                    Id = _currentTextId,
                    Delta = textDelta,
                }) is { } deltaError)
            {
                _err = new WrappedException(
                    "failed to enqueue text-delta event: " + deltaError.Message,
                    deltaError);
                return;
            }
        }

        if (delta?.ToolCalls is { Count: > 0 } toolCallDeltas)
        {
            if (_hasTextContentStarted)
            {
                if (EndTextContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (_hasReasoningContentStarted)
            {
                if (EndReasoningContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            foreach (var toolCallDelta in toolCallDeltas)
            {
                var toolCallId = toolCallDelta.Id;
                if (string.IsNullOrEmpty(toolCallId))
                {
                    toolCallId = GenerateId("tool");
                }

                if (!_activeToolCalls.TryGetValue(toolCallId, out var activeToolCall))
                {
                    activeToolCall = new ToolCall
                    {
                        Id = toolCallId,
                        Type = GoStr(toolCallDelta.Type),
                        Function = new FunctionCall
                        {
                            Name = toolCallDelta.Function.Name,
                            Arguments = string.Empty,
                        },
                    };
                    _activeToolCalls[toolCallId] = activeToolCall;

                    _hasToolContentStarted = true;

                    if (Enqueue(new AiSdkStreamEvent
                        {
                            Type = "tool-input-start",
                            ToolCallId = toolCallId,
                            ToolName = GoStr(toolCallDelta.Function.Name),
                        }) is { } startToolError)
                    {
                        _err = new WrappedException(
                            "failed to enqueue tool-input-start event: " + startToolError.Message,
                            startToolError);
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(toolCallDelta.Function.Arguments))
                {
                    activeToolCall.Function.Arguments +=
                        toolCallDelta.Function.Arguments;

                    if (Enqueue(new AiSdkStreamEvent
                        {
                            Type = "tool-input-delta",
                            ToolCallId = toolCallId,
                            InputTextDelta = toolCallDelta.Function.Arguments,
                        }) is { } deltaToolError)
                    {
                        _err = new WrappedException(
                            "failed to enqueue tool-input-delta event: " + deltaToolError.Message,
                            deltaToolError);
                        return;
                    }
                }
            }
        }

        if (choice.Message?.ToolCalls is { Count: > 0 } messageToolCalls)
        {
            foreach (var toolCall in messageToolCalls)
            {
                JsonElement input = default;
                var arguments = toolCall.Function.Arguments;
                if (arguments.Length > 0
                    && !AiSdkJson.TryParseAny(
                        Encoding.UTF8.GetBytes(arguments),
                        out input,
                        out var parseError))
                {
                    _err = new WrappedException(
                        "failed to unmarshal tool call arguments: " + parseError,
                        new JsonException(parseError));
                    return;
                }

                byte[] inputData;
                try
                {
                    inputData = AiSdkJson.MarshalAny(input);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    _err = new WrappedException("failed to marshal tool input: " + ex.Message, ex);
                    return;
                }

                if (Enqueue(new AiSdkStreamEvent
                    {
                        Type = "tool-input-available",
                        ToolCallId = GoStr(toolCall.Id),
                        ToolName = GoStr(toolCall.Function.Name),
                        Input = inputData,
                    }) is { } availableError)
                {
                    _err = new WrappedException(
                        "failed to enqueue tool-input-available event: " + availableError.Message,
                        availableError);
                    return;
                }
            }
        }

        if (choice.FinishReason is not null && !_hasFinished)
        {
            _hasFinished = true;

            if (_hasTextContentStarted)
            {
                if (EndTextContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (_hasReasoningContentStarted)
            {
                if (EndReasoningContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (_hasToolContentStarted)
            {
                if (EndToolContent() is { } error)
                {
                    _err = error;
                    return;
                }
            }

            if (Enqueue(new AiSdkStreamEvent { Type = "finish" }) is { } finishError)
            {
                _err = new WrappedException(
                    "failed to enqueue finish event: " + finishError.Message,
                    finishError);
            }
        }
    }

    private Exception? StartStep()
        => Enqueue(new AiSdkStreamEvent { Type = "start-step" });

    private Exception? FinishStep()
        => Enqueue(new AiSdkStreamEvent { Type = "finish-step" });

    private Exception? StartTextContent()
    {
        if (StartStep() is { } stepError)
        {
            return stepError;
        }

        _hasTextContentStarted = true;
        _currentTextId = GenerateId("text");

        return Enqueue(new AiSdkStreamEvent
        {
            Type = "text-start",
            Id = _currentTextId,
        });
    }

    private Exception? EndTextContent()
    {
        if (!_hasTextContentStarted)
        {
            return null;
        }

        _hasTextContentStarted = false;

        if (Enqueue(new AiSdkStreamEvent
            {
                Type = "text-end",
                Id = _currentTextId,
            }) is { } endError)
        {
            return endError;
        }

        return FinishStep();
    }

    private Exception? StartReasoningContent()
    {
        if (StartStep() is { } stepError)
        {
            return stepError;
        }

        _hasReasoningContentStarted = true;
        _currentReasoningId = GenerateId("reasoning");

        return Enqueue(new AiSdkStreamEvent
        {
            Type = "reasoning-start",
            Id = _currentReasoningId,
        });
    }

    private Exception? EndReasoningContent()
    {
        if (!_hasReasoningContentStarted)
        {
            return null;
        }

        _hasReasoningContentStarted = false;

        if (Enqueue(new AiSdkStreamEvent
            {
                Type = "reasoning-end",
                Id = _currentReasoningId,
            }) is { } endError)
        {
            return endError;
        }

        return FinishStep();
    }

    private Exception? EndToolContent()
    {
        if (!_hasToolContentStarted)
        {
            return null;
        }

        _hasToolContentStarted = false;
        return FinishStep();
    }

    private Exception? Enqueue(AiSdkStreamEvent streamEvent)
    {
        try
        {
            _queue.Add(new StreamEvent
            {
                Type = string.Empty,
                Data = GoJson.Marshal(streamEvent),
            });
            return null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new WrappedException("failed to marshal event data: " + ex.Message, ex);
        }
    }

    private static string GenerateId(string prefix)
        => prefix + "_" + Guid.NewGuid().ToString("N");

    private static string? GoStr(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
