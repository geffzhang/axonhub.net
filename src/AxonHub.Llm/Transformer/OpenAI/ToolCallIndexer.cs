using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.toolCallIndexer (tool_call_index.go): some upstreams
/// (Gemini's OpenAI-compatible endpoint) stream each parallel tool call whole
/// without <c>tool_calls[].index</c>. A call with a new id or name starts a new
/// call; a bare fragment, or one repeating the last call's id, continues it.
/// </summary>
internal sealed class ToolCallIndexer
{
    private readonly Dictionary<long, int> _next = [];
    private readonly Dictionary<long, int> _last = [];
    private readonly Dictionary<(long Choice, int Index), string> _ids = [];

    /// <summary>
    /// Port of applyToResponse: the unified response keeps the raw chunk's
    /// choices and tool calls in order.
    /// </summary>
    public void ApplyToResponse(byte[] data, LlmResponse response)
    {
        var raw = ParseRaw(data);
        for (var choicePos = 0; choicePos < response.Choices.Count; choicePos++)
        {
            var choice = response.Choices[choicePos];
            if (choice is null || choice.Delta?.ToolCalls is not { } toolCalls)
            {
                continue;
            }

            for (var pos = 0; pos < toolCalls.Count; pos++)
            {
                var toolCall = toolCalls[pos];
                toolCall.Index = Index(
                    raw,
                    choicePos,
                    choice.Index,
                    pos,
                    (int)toolCall.Index,
                    toolCall.Id ?? string.Empty,
                    toolCall.Function?.Name ?? string.Empty);
            }
        }
    }

    /// <summary>Port of applyToChunk over the wire response.</summary>
    public void ApplyToOpenAiResponse(byte[] data, OpenAiResponse response)
    {
        var raw = ParseRaw(data);
        if (response.Choices is null)
        {
            return;
        }

        for (var choicePos = 0; choicePos < response.Choices.Count; choicePos++)
        {
            var choice = response.Choices[choicePos];
            if (choice is null || choice.Delta?.ToolCalls is not { } toolCalls)
            {
                continue;
            }

            for (var pos = 0; pos < toolCalls.Count; pos++)
            {
                var toolCall = toolCalls[pos];
                toolCall.Index = Index(
                    raw,
                    choicePos,
                    choice.Index,
                    pos,
                    (int)toolCall.Index,
                    toolCall.Id ?? string.Empty,
                    toolCall.Function?.Name ?? string.Empty);
            }
        }
    }

    private int Index(
        JsonDocument? raw,
        int choicePos,
        long choiceIndex,
        int pos,
        int index,
        string id,
        string name)
    {
        if (HasExplicitIndex(raw, choicePos, pos))
        {
            _next[choiceIndex] = Math.Max(Next(choiceIndex), index + 1);
        }
        else
        {
            var continues = Next(choiceIndex) > 0
                && ((id.Length == 0 && name.Length == 0)
                    || (id.Length > 0
                        && _last.TryGetValue(choiceIndex, out var last)
                        && _ids.TryGetValue((choiceIndex, last), out var lastId)
                        && id == lastId));

            if (continues)
            {
                index = _last[choiceIndex];
            }
            else
            {
                index = Next(choiceIndex);
                _next[choiceIndex] = index + 1;
            }
        }

        _last[choiceIndex] = index;
        if (id.Length > 0)
        {
            _ids[(choiceIndex, index)] = id;
        }

        return index;
    }

    private int Next(long choiceIndex)
        => _next.TryGetValue(choiceIndex, out var next) ? next : 0;

    private static bool HasExplicitIndex(JsonDocument? raw, int choicePos, int pos)
    {
        if (raw is null
            || raw.RootElement.ValueKind != JsonValueKind.Object
            || !raw.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choicePos >= choices.GetArrayLength())
        {
            return false;
        }

        var choice = choices[choicePos];
        return choice.ValueKind == JsonValueKind.Object
            && choice.TryGetProperty("delta", out var delta)
            && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array
            && pos < toolCalls.GetArrayLength()
            && toolCalls[pos].ValueKind == JsonValueKind.Object
            && toolCalls[pos].TryGetProperty("index", out _);
    }

    private static JsonDocument? ParseRaw(byte[] data)
    {
        try
        {
            return JsonDocument.Parse(
                GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
