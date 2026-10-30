using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openaiInboundStream (llm/transformer/openai/inbound_stream.go):
/// watches the unified chat stream and, when it ends without a terminal event
/// but with usage reported, synthesizes the missing finish chunk and [DONE].
/// </summary>
public sealed class OpenAiInboundStream : IStream<LlmResponse>
{
    private readonly IStream<LlmResponse> _source;
    private readonly Queue<LlmResponse> _queue = [];
    private readonly Dictionary<int, ChoiceState> _choices = [];
    private bool _usage;
    private bool _done;
    private bool _ended;
    private string _id = string.Empty;
    private string _model = string.Empty;
    private long _created;

    public OpenAiInboundStream(IStream<LlmResponse> source)
    {
        _source = source;
    }

    public LlmResponse? Current { get; private set; }

    public Exception? Err => _source.Err;

    public bool Next()
    {
        if (_queue.Count > 0)
        {
            Current = _queue.Dequeue();
            return true;
        }

        if (_ended)
        {
            return false;
        }

        while (_source.Next())
        {
            if (_source.Current is { } chunk)
            {
                Current = chunk;
                Observe(chunk);
                return true;
            }
        }

        _ended = true;

        if (_source.Err is not null || _done || !_usage)
        {
            return false;
        }

        SynthesizeTerminalChunk();
        return Next();
    }

    public void Dispose() => _source.Dispose();

    private void Observe(LlmResponse response)
    {
        if (response.Object == "[DONE]")
        {
            _done = true;
            return;
        }

        if (response.Id.Length > 0)
        {
            _id = response.Id;
        }

        if (response.Model.Length > 0)
        {
            _model = response.Model;
        }

        if (response.Created != 0)
        {
            _created = response.Created;
        }

        if (response.Usage is not null)
        {
            _usage = true;
        }

        foreach (var choice in response.Choices)
        {
            if (!_choices.TryGetValue(choice.Index, out var state))
            {
                state = new ChoiceState();
                _choices[choice.Index] = state;
            }

            if (choice.FinishReason is not null && choice.FinishReason.Length > 0)
            {
                state.Finished = true;
            }

            foreach (var message in new[] { choice.Delta, choice.Message })
            {
                if (message is null)
                {
                    continue;
                }

                if ((message.Content?.Content is not null && message.Content.Content.Length > 0)
                    || (message.Content?.MultipleContent?.Count ?? 0) > 0
                    || (message.ReasoningContent is not null && message.ReasoningContent.Length > 0)
                    || (message.Reasoning is not null && message.Reasoning.Length > 0)
                    || (message.Refusal is not null && message.Refusal.Length > 0)
                    || message.Audio is not null)
                {
                    state.Output = true;
                }

                foreach (var tool in message.ToolCalls ?? [])
                {
                    state.Output = true;

                    if (!state.Tools.TryGetValue(tool.Index, out var acc))
                    {
                        acc = new ToolCall { Function = new FunctionCall() };
                        state.Tools[tool.Index] = acc;
                    }

                    if (tool.Function.Name.Length > 0)
                    {
                        acc.Function.Name = tool.Function.Name;
                    }

                    acc.Function.Arguments += tool.Function.Arguments;
                }
            }
        }
    }

    /// <summary>Port of openaiInboundStream.finalize; renamed to avoid the
    /// Object.Finalize destructor clash.</summary>
    private void SynthesizeTerminalChunk()
    {
        var indexes = new List<int>();

        foreach (var (index, choice) in _choices)
        {
            if (!choice.Output)
            {
                return;
            }

            foreach (var tool in choice.Tools.Values)
            {
                // Go only synthesizes when every accumulated tool call has a
                // name and arguments that parse as JSON.
                if (string.IsNullOrWhiteSpace(tool.Function.Name)
                    || GoJsonSyntax.Validate(Encoding.UTF8.GetBytes(tool.Function.Arguments)) is not null)
                {
                    return;
                }
            }

            if (!choice.Finished)
            {
                indexes.Add(index);
            }
        }

        if (indexes.Count == 0)
        {
            return;
        }

        indexes.Sort();

        var choices = new List<Choice>(indexes.Count);
        foreach (var index in indexes)
        {
            var reason = _choices[index].Tools.Count > 0 ? "tool_calls" : "stop";
            choices.Add(new Choice
            {
                Index = index,
                Delta = new Message(),
                FinishReason = reason,
            });
        }

        _queue.Enqueue(new LlmResponse
        {
            Id = _id,
            Model = _model,
            Created = _created,
            Object = "chat.completion.chunk",
            Choices = choices,
            StreamCompletionEvidence = StreamCompletionEvidence.OpenAiChatCleanEof,
        });

        _queue.Enqueue(LlmResponse.Done());
    }

    private sealed class ChoiceState
    {
        public bool Output { get; set; }

        public bool Finished { get; set; }

        public Dictionary<int, ToolCall> Tools { get; } = [];
    }
}

/// <summary>Port of the chat half of InboundTransformer.TransformStream.</summary>
public sealed partial class OpenAiInboundTransformer
{
    /// <summary>Port of TransformStream: unified stream to client SSE stream.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
    {
        var watched = new OpenAiInboundStream(stream);
        return new MapStream<LlmResponse, StreamEvent>(watched, TransformStreamChunk);
    }

    /// <summary>Port of InboundTransformer.AggregateStreamChunks (inbound.go:182-187).</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
        => OpenAiAggregator.AggregateStreamChunks(chunks, OpenAiAggregator.DefaultTransformChunk, out meta);

    /// <summary>
    /// Port of TransformStreamChunk: unified response to one client SSE event.
    /// Returns null for events the reference filters (<c>streams.NoNil</c>).
    /// </summary>
    public StreamEvent? TransformStreamChunk(LlmResponse? chatResp)
    {
        if (chatResp is null)
        {
            throw new InvalidOperationException("chat completion response is nil");
        }

        if (chatResp.Object == "[DONE]")
        {
            return new StreamEvent { Data = Encoding.UTF8.GetBytes("[DONE]") };
        }

        // OpenAI format does not support ReasoningSignature in streaming, so a
        // pure signature event is dropped.
        if (IsReasoningSignatureEvent(chatResp))
        {
            return null;
        }

        return new StreamEvent
        {
            Type = string.Empty,
            Data = GoJson.Marshal(OpenAiConvertFromLlm.ResponseFromLlm(chatResp)),
            CleanEofCompletionEvidence = chatResp.StreamCompletionEvidence == StreamCompletionEvidence.OpenAiChatCleanEof,
        };
    }

    /// <summary>Port of isReasoningSignatureEvent.</summary>
    public static bool IsReasoningSignatureEvent(LlmResponse resp)
    {
        if (resp.Choices.Count != 1)
        {
            return false;
        }

        var delta = resp.Choices[0].Delta;
        if (delta is null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(delta.ReasoningSignature))
        {
            return false;
        }

        var hasContent = delta.Content?.Content is not null || (delta.Content?.MultipleContent?.Count ?? 0) > 0;
        var hasReasoningContent = !string.IsNullOrEmpty(delta.ReasoningContent);
        var hasToolCalls = (delta.ToolCalls?.Count ?? 0) > 0;
        var hasRefusal = !string.IsNullOrEmpty(delta.Refusal);

        return !hasContent && !hasReasoningContent && !hasToolCalls && !hasRefusal;
    }
}
