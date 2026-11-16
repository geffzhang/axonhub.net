using AxonHub.Llm;

namespace AxonHub.Llm.Transformer.Shared;

/// <summary>
/// Port of llm/transformer/shared/messages.go: message normalization shared by
/// the orchestrator and the outbound transformers.
/// </summary>
public static class SharedMessageFilter
{
    /// <summary>
    /// Port of <c>FilterOutResponseCustomToolMessages</c>
    /// (transformer/shared/messages.go:13-60): remove Responses custom-tool
    /// calls and their result messages so the conversation can be rendered by
    /// a non-Responses outbound protocol. A message whose tool calls all
    /// disappear is dropped when it carries no other content.
    /// </summary>
    public static List<Message> FilterOutResponseCustomToolMessages(
        IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return [];
        }

        var removedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var filtered = new List<Message>(messages.Count);

        foreach (var message in messages)
        {
            if (message.Role == "tool"
                && message.ToolCallId is { } toolCallId
                && removedToolCallIds.Contains(toolCallId))
            {
                continue;
            }

            if (message.ToolCalls is not { Count: > 0 } toolCalls)
            {
                filtered.Add(message);
                continue;
            }

            // Go copies the Message struct before rewriting its tool calls.
            var cloned = message.ShallowClone();
            cloned.ToolCalls = new List<ToolCall>(toolCalls.Count);

            foreach (var toolCall in toolCalls)
            {
                if (toolCall.Type == ToolTypes.ResponsesCustomTool
                    || toolCall.ResponseCustomToolCall is not null)
                {
                    if (!string.IsNullOrEmpty(toolCall.Id))
                    {
                        removedToolCallIds.Add(toolCall.Id);
                    }

                    if (toolCall.ResponseCustomToolCall is { } custom
                        && !string.IsNullOrEmpty(custom.CallId))
                    {
                        removedToolCallIds.Add(custom.CallId);
                    }

                    continue;
                }

                cloned.ToolCalls.Add(toolCall);
            }

            if (ShouldDropMessageAfterToolFiltering(cloned))
            {
                continue;
            }

            filtered.Add(cloned);
        }

        return filtered;
    }

    /// <summary>Port of <c>shouldDropMessageAfterToolFiltering</c> (messages.go:62-76).</summary>
    private static bool ShouldDropMessageAfterToolFiltering(Message message)
    {
        if (message.ToolCalls is { Count: > 0 })
        {
            return false;
        }

        if (message.Content is { } content
            && (content.Content is not null || content.MultipleContent is { Count: > 0 }))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(message.Refusal)
            || message.ToolCallId is not null
            || message.ReasoningContent is not null
            || message.Reasoning is not null
            || message.Audio is not null)
        {
            return false;
        }

        return true;
    }
}
