using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>
/// Port of geminiInboundStream (inbound_stream.go:39-326): a stateful
/// transformer that aggregates tool call deltas — Gemini clients expect each
/// streamed functionCall part to carry a complete args object, while
/// OpenAI/Anthropic-compatible streams may send partial deltas.
/// </summary>
public sealed class GeminiInboundStream : IStream<StreamEvent>
{
    private readonly GeminiInboundTransformer _transformer;
    private readonly IStream<LlmResponse> _source;

    private StreamEvent? _current;
    private Exception? _err;

    // choiceIndex -> toolCallIndex -> aggregator
    private readonly Dictionary<long, Dictionary<long, ToolCallAgg>> _pendingToolCallsByC = [];
    // choiceIndex -> last seen reasoning signature delta (may arrive as a standalone chunk)
    private readonly Dictionary<long, string> _pendingReasoningSignatureByC = [];

    public GeminiInboundStream(GeminiInboundTransformer transformer, IStream<LlmResponse> source)
    {
        _transformer = transformer;
        _source = source;
    }

    public StreamEvent? Current => _current;

    public Exception? Err => _err ?? _source.Err;

    public bool Next()
    {
        if (_err is not null)
        {
            return false;
        }

        while (_source.Next())
        {
            StreamEvent? evt;
            try
            {
                evt = TransformChunk(_source.Current);
            }
            catch (Exception ex)
            {
                _err = ex;
                return false;
            }

            if (evt is null)
            {
                continue;
            }

            _current = evt;
            return true;
        }

        if (_source.Err is { } err)
        {
            _err = err;
        }

        return false;
    }

    public void Dispose() => _source.Dispose();

    /// <summary>
    /// Port of transformChunk (inbound_stream.go:104-326): one unified chunk
    /// may become at most one Gemini StreamEvent; tool call deltas are
    /// buffered per (choice, index) until they form a complete call.
    /// </summary>
    private StreamEvent? TransformChunk(LlmResponse? chunk)
    {
        if (chunk is null)
        {
            return null;
        }

        // Handle [DONE] marker
        if (chunk.Object == "[DONE]")
        {
            return null;
        }

        // Fast-path: no choices
        if (chunk.Choices.Count == 0)
        {
            return _transformer.TransformStreamChunk(chunk);
        }

        var @out = new LlmResponse
        {
            Id = chunk.Id,
            Object = chunk.Object,
            Created = chunk.Created,
            Model = chunk.Model,
            SystemFingerprint = chunk.SystemFingerprint,
            ServiceTier = chunk.ServiceTier,
            Usage = chunk.Usage,
            Error = chunk.Error,
            RequestType = chunk.RequestType,
            TransformerMetadata = chunk.TransformerMetadata is null
                ? null
                : new Dictionary<string, JsonElement>(chunk.TransformerMetadata),
            Choices = new List<Choice>(chunk.Choices.Count),
        };

        var emitAny = false;

        foreach (var choice in chunk.Choices)
        {
            var outChoice = new Choice
            {
                Index = choice.Index,
                FinishReason = choice.FinishReason,
                Logprobs = choice.Logprobs,
                TransformerMetadata = choice.TransformerMetadata is null
                    ? null
                    : new Dictionary<string, JsonElement>(choice.TransformerMetadata),
            };

            var srcMsg = choice.Delta;
            var targetIsDelta = true;

            if (srcMsg is null)
            {
                srcMsg = choice.Message;
                targetIsDelta = false;
            }

            if (srcMsg is null)
            {
                @out.Choices.Add(outChoice);
                continue;
            }

            // Copy message but strip tool calls; we will re-inject only completed ones.
            var dstMsg = srcMsg.ShallowClone();
            dstMsg.ToolCalls = null;

            var choiceIndex = choice.Index;
            if (!_pendingToolCallsByC.TryGetValue(choiceIndex, out var pendingByIndex))
            {
                pendingByIndex = [];
                _pendingToolCallsByC[choiceIndex] = pendingByIndex;
            }

            // Buffer a signature-only delta for later: Anthropic streaming may deliver the
            // signature in a standalone chunk (signature_delta) between tool call chunks.
            if (dstMsg.ReasoningSignature is { Length: > 0 } signature)
            {
                _pendingReasoningSignatureByC[choiceIndex] = signature;
            }
            else if (_pendingReasoningSignatureByC.TryGetValue(choiceIndex, out var pendingSignature)
                && pendingSignature.Length > 0)
            {
                // Carry the pending signature forward so conversion can attach it to a Gemini part.
                dstMsg.ReasoningSignature = pendingSignature;
            }

            // Accumulate tool call deltas.
            foreach (var tc in srcMsg.ToolCalls ?? [])
            {
                var idx = tc.Index;

                if (!pendingByIndex.TryGetValue(idx, out var agg))
                {
                    agg = new ToolCallAgg { Index = idx };
                    pendingByIndex[idx] = agg;
                }

                if (tc.Id is { Length: > 0 } id)
                {
                    agg.Id = id;
                }

                if (tc.Type is { Length: > 0 } typ)
                {
                    agg.Typ = typ;
                }

                if (tc.Function.Name is { Length: > 0 } name)
                {
                    agg.Name = name;
                }

                if (tc.Function.Arguments is { Length: > 0 } args)
                {
                    agg.Arguments += args;
                }

                if (tc.TransformerMetadata is { } tcMetadata)
                {
                    agg.TransformerMetadata ??= new Dictionary<string, JsonElement>();
                    foreach (var (key, value) in tcMetadata)
                    {
                        agg.TransformerMetadata[key] = value;
                    }
                }
            }

            // Decide if we should flush tool calls:
            // - If any pending tool call has valid JSON args, flush that tool call.
            // - If finish_reason == "tool_calls", flush all pending tool calls (repairing JSON if needed).
            var flushAll = choice.FinishReason is "tool_calls";

            var completed = new List<ToolCall>();

            // Iterate tool calls in index order for deterministic output.
            foreach (var idx in pendingByIndex.Keys.OrderBy(static i => i).ToArray())
            {
                var agg = pendingByIndex[idx];
                var args = agg.Arguments;

                // Gemini requires function_call.name, so don't emit tool calls until we have it.
                if (agg.Name.Length == 0)
                {
                    continue;
                }

                // If arguments were never provided, don't emit early. We only know it's a
                // no-arg tool call when the tool_calls turn ends.
                if (args.Trim().Length == 0)
                {
                    if (!flushAll)
                    {
                        continue;
                    }

                    args = "{}";
                }

                if (flushAll && args.Length > 0
                    && GoJsonSyntax.Validate(Encoding.UTF8.GetBytes(args)) is not null)
                {
                    args = Encoding.UTF8.GetString(AnthropicJson.SafeJsonRawMessage(args));
                }

                var isValidNow = GoJsonSyntax.Validate(Encoding.UTF8.GetBytes(args)) is null;
                if (!flushAll && !isValidNow)
                {
                    continue;
                }

                completed.Add(new AxonHub.Llm.ToolCall
                {
                    Id = agg.Id,
                    Type = agg.Typ,
                    Function = new AxonHub.Llm.FunctionCall { Name = agg.Name, Arguments = args },
                    Index = agg.Index,
                    TransformerMetadata = agg.TransformerMetadata,
                });

                pendingByIndex.Remove(idx);
            }

            if (completed.Count > 0)
            {
                dstMsg.ToolCalls = completed;
            }

            if (targetIsDelta)
            {
                outChoice.Delta = dstMsg;
            }
            else
            {
                outChoice.Message = dstMsg;
            }

            // Emit if there is any non-tool content, any flushed tool call, or a finish reason.
            var hasNonToolContent = (dstMsg.Content?.Content is { Length: > 0 })
                || (dstMsg.Content?.MultipleContent is { Count: > 0 })
                || (dstMsg.ReasoningContent is { Length: > 0 })
                || dstMsg.Refusal is { Length: > 0 };

            if (hasNonToolContent || completed.Count > 0 || outChoice.FinishReason is not null)
            {
                emitAny = true;

                // Once we've emitted a chunk that can carry the signature, clear the pending
                // buffer. This avoids re-attaching the same signature onto subsequent chunks.
                if (dstMsg.ReasoningSignature is { Length: > 0 })
                {
                    _pendingReasoningSignatureByC.Remove(choiceIndex);
                }
            }

            @out.Choices.Add(outChoice);
        }

        if (!emitAny)
        {
            return null;
        }

        var geminiResp = GeminiInboundConvert.ConvertLLMToGeminiResponse(@out, true);
        var eventData = GoJson.Marshal(geminiResp);

        return new StreamEvent
        {
            Data = eventData,
        };
    }

    private sealed class ToolCallAgg
    {
        public long Index;
        public string Id = string.Empty;
        public string Typ = string.Empty;
        public string Name = string.Empty;
        public string Arguments = string.Empty;
        public Dictionary<string, JsonElement>? TransformerMetadata;
    }
}
