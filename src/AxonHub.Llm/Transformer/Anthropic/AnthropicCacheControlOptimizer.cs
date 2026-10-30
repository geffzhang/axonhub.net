using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of llm/transformer/anthropic/ensure_cache_control.go: keeps the
/// request within Anthropic's four cache_control breakpoints, preferring
/// structural anchors (tools, system) and then the conversation tail.
/// </summary>
public static class AnthropicCacheControlOptimizer
{
    /// <summary>Maximum number of cache_control breakpoints Anthropic allows.</summary>
    public const int MaxCacheControlBreakpoints = 4;

    /// <summary>Block-count window used to place the second message anchor.</summary>
    public const int AdaptiveCacheControlBlockWindow = 20;

    private const int CacheControlSectionTools = 0;
    private const int CacheControlSectionSystem = 1;
    private const int CacheControlSectionMessages = 2;

    /// <summary>Port of optimizeCacheControl.</summary>
    public static void Optimize(AnthropicMessageRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        NormalizeMessageContents(req);
        SanitizeUnsupportedCacheControls(req);
        TrimCacheControlsToLimit(req, MaxCacheControlBreakpoints);

        var remaining = MaxCacheControlBreakpoints - CountCacheControls(req);
        if (remaining <= 0)
        {
            return;
        }

        EnsureStructuralCacheControls(req, remaining);
        if (CountMessageBreakpoints(req) > 0)
        {
            return;
        }

        remaining = MaxCacheControlBreakpoints - CountCacheControls(req);
        var refs = CollectMessageBlockRefs(req);
        var messageAnchors = Math.Min(DesiredMessageCacheAnchors(refs.Count), remaining);
        InjectPlannedMessageCacheControls(refs, messageAnchors);
    }

    /// <summary>
    /// Counts the cache_control breakpoints already present on messages
    /// (excluding tools and system).
    /// </summary>
    public static int CountMessageBreakpoints(AnthropicMessageRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var count = 0;
        foreach (var message in req.Messages ?? [])
        {
            count += CountContentCacheControls(message.Content);
        }

        return count;
    }

    /// <summary>
    /// Trims message breakpoints first to preserve the existing policy, then
    /// deterministically trims structural-only overflow.
    /// </summary>
    public static void TrimCacheControlsToLimit(AnthropicMessageRequest req, int limit)
    {
        ArgumentNullException.ThrowIfNull(req);

        while (CountCacheControls(req) > limit)
        {
            if (!RemoveEarliestMessageBreakpoint(req) && !RemoveEarliestStructuralBreakpoint(req))
            {
                return;
            }
        }
    }

    private static bool RemoveEarliestMessageBreakpoint(AnthropicMessageRequest req)
    {
        foreach (var message in req.Messages ?? [])
        {
            if (RemoveEarliestContentBreakpoint(message.Content))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RemoveEarliestContentBreakpoint(AnthropicMessageContent? content)
    {
        if (content is null)
        {
            return false;
        }

        if (content.Raw is { Length: > 0 })
        {
            return RemoveEarliestRawCacheControl(content);
        }

        foreach (var block in content.MultipleContent ?? [])
        {
            if (block.CacheControl is not null)
            {
                block.CacheControl = null;
                return true;
            }

            if (block.Content is not null && RemoveEarliestContentBreakpoint(block.Content))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RemoveEarliestStructuralBreakpoint(AnthropicMessageRequest req)
    {
        foreach (var tool in req.Tools ?? [])
        {
            if (tool.CacheControl is not null)
            {
                tool.CacheControl = null;
                return true;
            }
        }

        if (req.System?.MultiplePrompts is { } systemParts)
        {
            foreach (var part in systemParts)
            {
                if (part.CacheControl is not null)
                {
                    part.CacheControl = null;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Normalizes plain string message content into the multiple-content array
    /// form before any other operation, so no later function causes an implicit
    /// structural rewrite.
    /// </summary>
    public static void NormalizeMessageContents(AnthropicMessageRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        foreach (var message in req.Messages ?? [])
        {
            var content = message.Content;
            if (content is null)
            {
                continue;
            }

            if ((content.MultipleContent is null || content.MultipleContent.Count == 0)
                && content.Content is { Length: > 0 } text)
            {
                content.Content = null;
                content.MultipleContent =
                [
                    new AnthropicMessageContentBlock { Type = "text", Text = text },
                ];
            }
        }
    }

    /// <summary>
    /// Fills tools and system within the global budget. Generated 5m anchors are
    /// never placed before a client 1h anchor.
    /// </summary>
    private static void EnsureStructuralCacheControls(AnthropicMessageRequest req, int remaining)
    {
        var oneHourSection = LastOneHourCacheControlSection(req);

        var tools = req.Tools ?? [];
        if (remaining > 0
            && oneHourSection <= CacheControlSectionTools
            && tools.Count > 0
            && !AnyToolHasCacheControl(req))
        {
            tools[^1].CacheControl = new AnthropicCacheControl { Type = "ephemeral" };
            remaining--;
        }

        if (remaining <= 0 || oneHourSection > CacheControlSectionSystem || req.System is null)
        {
            return;
        }

        if (req.System.MultiplePrompts is { Count: > 0 } systemParts)
        {
            if (!AnySystemPromptHasCacheControl(req))
            {
                systemParts[^1].CacheControl = new AnthropicCacheControl { Type = "ephemeral" };
            }

            return;
        }

        // String-form system prompts are normalized to the array form so the
        // cache_control lands on an array element.
        if (req.System.Prompt is { Length: > 0 } prompt)
        {
            req.System.Prompt = null;
            req.System.MultiplePrompts =
            [
                new AnthropicSystemPromptPart
                {
                    Type = "text",
                    Text = prompt,
                    CacheControl = new AnthropicCacheControl { Type = "ephemeral" },
                },
            ];
        }
    }

    private static bool AnyToolHasCacheControl(AnthropicMessageRequest req)
        => req.Tools?.Any(static tool => tool.CacheControl is not null) == true;

    private static bool AnySystemPromptHasCacheControl(AnthropicMessageRequest req)
        => req.System?.MultiplePrompts?.Any(static part => part.CacheControl is not null) == true;

    private static int LastOneHourCacheControlSection(AnthropicMessageRequest req)
    {
        var section = -1;

        foreach (var tool in req.Tools ?? [])
        {
            if (tool.CacheControl is not null && tool.CacheControl.Ttl == "1h")
            {
                section = CacheControlSectionTools;
            }
        }

        if (req.System?.MultiplePrompts is { } systemParts)
        {
            foreach (var part in systemParts)
            {
                if (part.CacheControl is not null && part.CacheControl.Ttl == "1h")
                {
                    section = CacheControlSectionSystem;
                }
            }
        }

        foreach (var message in req.Messages ?? [])
        {
            if (ContentHasOneHourCacheControl(message.Content))
            {
                return CacheControlSectionMessages;
            }
        }

        return section;
    }

    /// <summary>
    /// Clears cache_control from blocks Anthropic forbids caching (thinking
    /// blocks and empty text blocks).
    /// </summary>
    private static void SanitizeUnsupportedCacheControls(AnthropicMessageRequest req)
    {
        foreach (var message in req.Messages ?? [])
        {
            SanitizeContentCacheControls(message.Content);
        }
    }

    private static void SanitizeContentCacheControls(AnthropicMessageContent? content)
    {
        if (content is null || content.Raw is { Length: > 0 })
        {
            return;
        }

        foreach (var block in content.MultipleContent ?? [])
        {
            if (!IsCacheableMessageBlock(block) && block.CacheControl is not null)
            {
                block.CacheControl = null;
            }

            if (block.Content is not null)
            {
                SanitizeContentCacheControls(block.Content);
            }
        }
    }

    private static int DesiredMessageCacheAnchors(int cacheableBlocks)
    {
        if (cacheableBlocks == 0)
        {
            return 0;
        }

        if (cacheableBlocks >= AdaptiveCacheControlBlockWindow)
        {
            return 2;
        }

        return 1;
    }

    private static void InjectPlannedMessageCacheControls(List<CacheControlRef> refs, int target)
    {
        if (target <= 0 || refs.Count == 0)
        {
            return;
        }

        // First priority: the last cacheable block (the recommended breakpoint
        // at the end of the conversation).
        refs[^1].Value = new AnthropicCacheControl { Type = "ephemeral" };

        // Second priority (only when multiple message anchors are wanted): the
        // window boundary 20 blocks before the end.
        if (target > 1)
        {
            var index = PickWindowAnchorIndex(refs, AdaptiveCacheControlBlockWindow);
            if (index >= 0)
            {
                refs[index].Value = new AnthropicCacheControl { Type = "ephemeral" };
            }
        }
    }

    /// <summary>
    /// Picks the unmarked anchor at the window boundary, preferring the left
    /// (more stable prefix) and falling back to the right.
    /// </summary>
    private static int PickWindowAnchorIndex(List<CacheControlRef> refs, int window)
    {
        if (refs.Count == 0)
        {
            return -1;
        }

        if (window < 0)
        {
            window = 0;
        }

        var target = Math.Max(refs.Count - 1 - window, 0);

        for (var i = target; i >= 0; i--)
        {
            if (refs[i].Value is not null)
            {
                continue;
            }

            return i;
        }

        for (var i = target + 1; i < refs.Count; i++)
        {
            if (refs[i].Value is not null)
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// Collects references to every cacheable block in the messages. Callers
    /// must run <see cref="NormalizeMessageContents"/> first; this function does
    /// not rewrite structure.
    /// </summary>
    private static List<CacheControlRef> CollectMessageBlockRefs(AnthropicMessageRequest req)
    {
        var refs = new List<CacheControlRef>();

        foreach (var message in req.Messages ?? [])
        {
            AppendCacheControlRefs(refs, message.Content);
        }

        return refs;
    }

    private static void AppendCacheControlRefs(
        List<CacheControlRef> refs,
        AnthropicMessageContent? content)
    {
        if (content is null || content.Raw is { Length: > 0 })
        {
            return;
        }

        foreach (var block in content.MultipleContent ?? [])
        {
            if (IsCacheableMessageBlock(block))
            {
                refs.Add(new CacheControlRef(
                    () => block.CacheControl,
                    value => block.CacheControl = value));
            }

            if (block.Content is not null)
            {
                AppendCacheControlRefs(refs, block.Content);
            }
        }
    }

    /// <summary>Counts all cache_control breakpoints in tools/system/messages.</summary>
    public static int CountCacheControls(AnthropicMessageRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var count = 0;

        foreach (var tool in req.Tools ?? [])
        {
            if (tool.CacheControl is not null)
            {
                count++;
            }
        }

        foreach (var part in req.System?.MultiplePrompts ?? [])
        {
            if (part.CacheControl is not null)
            {
                count++;
            }
        }

        foreach (var message in req.Messages ?? [])
        {
            count += CountContentCacheControls(message.Content);
        }

        return count;
    }

    private static int CountContentCacheControls(AnthropicMessageContent? content)
    {
        if (content is null)
        {
            return 0;
        }

        if (content.Raw is { Length: > 0 })
        {
            object? value;
            try
            {
                value = DecodeRawContent(content.Raw);
            }
            catch (JsonException)
            {
                return 0;
            }

            var count = 0;
            WalkRawContent(value, block =>
            {
                if (block.TryGetValue("cache_control", out var cacheControl) && cacheControl is not null)
                {
                    count++;
                }

                return false;
            });

            return count;
        }

        var result = 0;
        foreach (var block in content.MultipleContent ?? [])
        {
            if (IsCacheableMessageBlock(block) && block.CacheControl is not null)
            {
                result++;
            }

            if (block.Content is not null)
            {
                result += CountContentCacheControls(block.Content);
            }
        }

        return result;
    }

    private static bool ContentHasOneHourCacheControl(AnthropicMessageContent? content)
    {
        if (content is null)
        {
            return false;
        }

        if (content.Raw is { Length: > 0 })
        {
            object? value;
            try
            {
                value = DecodeRawContent(content.Raw);
            }
            catch (JsonException)
            {
                return false;
            }

            var found = false;
            WalkRawContent(value, block =>
            {
                if (block.TryGetValue("cache_control", out var cacheControl)
                    && cacheControl is Dictionary<string, object?> control
                    && control.TryGetValue("ttl", out var ttl)
                    && ttl is string ttlText
                    && ttlText == "1h")
                {
                    found = true;
                    return true;
                }

                return false;
            });

            return found;
        }

        foreach (var block in content.MultipleContent ?? [])
        {
            if (block.CacheControl is not null && block.CacheControl.Ttl == "1h")
            {
                return true;
            }

            if (block.Content is not null && ContentHasOneHourCacheControl(block.Content))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RemoveEarliestRawCacheControl(AnthropicMessageContent content)
    {
        if (content.Raw is not { Length: > 0 } raw)
        {
            return false;
        }

        object? value;
        try
        {
            value = DecodeRawContent(raw);
        }
        catch (JsonException)
        {
            return false;
        }

        var removed = WalkRawContent(value, block =>
        {
            if (!block.TryGetValue("cache_control", out var cacheControl) || cacheControl is null)
            {
                return false;
            }

            block.Remove("cache_control");
            return true;
        });

        if (!removed)
        {
            return false;
        }

        byte[] updated;
        try
        {
            updated = GoJson.Marshal(value);
        }
        catch (JsonException)
        {
            return false;
        }

        List<AnthropicMessageContentBlock>? blocks;
        try
        {
            blocks = LlmJson.Deserialize<List<AnthropicMessageContentBlock>>(updated);
        }
        catch (JsonException)
        {
            return false;
        }

        content.Raw = updated;
        content.MultipleContent = blocks;

        return true;
    }

    /// <summary>
    /// Decodes raw content into Go-like <c>any</c> values: objects become
    /// insertion-ordered maps, arrays lists, strings CLR strings, numbers raw
    /// JSON (Go's <c>UseNumber</c>) and null a CLR null.
    /// </summary>
    private static object? DecodeRawContent(byte[] raw)
    {
        using var document = JsonDocument.Parse(raw);
        return ElementToValue(document.RootElement);
    }

    private static object? ElementToValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = ElementToValue(property.Value);
                }

                return map;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(ElementToValue(item));
                }

                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                // Numbers and booleans keep their raw literal, like UseNumber.
                return element.Clone();
        }
    }

    private static bool WalkRawContent(
        object? value,
        Func<Dictionary<string, object?>, bool> visit)
    {
        switch (value)
        {
            case List<object?> list:
                foreach (var item in list)
                {
                    if (WalkRawContent(item, visit))
                    {
                        return true;
                    }
                }

                break;
            case Dictionary<string, object?> map:
                if (visit(map))
                {
                    return true;
                }

                if (map.TryGetValue("content", out var nested))
                {
                    return WalkRawContent(nested, visit);
                }

                break;
        }

        return false;
    }

    private static bool IsCacheableMessageBlock(AnthropicMessageContentBlock block)
        => block.Type switch
        {
            "thinking" or "redacted_thinking" => false,
            "text" => !string.IsNullOrEmpty(block.Text),
            _ => true,
        };

    /// <summary>
    /// Go's <c>**CacheControl</c>: a reference to the property so injection can
    /// replace it in place.
    /// </summary>
    private sealed class CacheControlRef(Func<AnthropicCacheControl?> get, Action<AnthropicCacheControl?> set)
    {
        public AnthropicCacheControl? Value
        {
            get => get();
            set => set(value);
        }
    }
}
