using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of the request half of llm/transformer/anthropic/outbound_convert.go:
/// the unified request becomes an Anthropic Messages request, including the
/// thinking/effort resolution table, tool conversion and the message
/// normalisation that keeps tool results next to the assistant turn.
/// </summary>
public static class AnthropicOutboundRequest
{
    /// <summary>Native Anthropic web search tool type (tools.go).</summary>
    public const string ToolTypeWebSearch20250305 = AnthropicToolFilter.ToolTypeWebSearch20250305;

    /// <summary>Standard function name that maps to the native tool.</summary>
    public const string WebSearchFunctionName = "web_search";

    /// <summary>
    /// Port of <c>anthropic.TransformerMetadataKeyOutputConfigEffort</c>
    /// (tool_blocks.go:16): the client's native <c>output_config.effort</c>,
    /// kept so the outbound rebuilds it after the unified effort is mapped.
    /// </summary>
    public const string TransformerMetadataKeyOutputConfigEffort =
        AnthropicJson.KeyOutputConfigEffort;

    /// <summary>
    /// Port of <c>anthropic.TransformerMetadataKeyThinkingType</c>
    /// (tool_blocks.go): the client's native thinking type marker, used to
    /// round-trip <c>between_tools</c> without re-deriving it.
    /// </summary>
    public const string TransformerMetadataKeyThinkingType =
        AnthropicJson.KeyThinkingType;

    private const long DefaultAnthropicMaxTokens = 8192;

    private static readonly Dictionary<string, long> DefaultReasoningEffortMapping = new(StringComparer.Ordinal)
    {
        [ReasoningEfforts.Minimal] = 5000,
        [ReasoningEfforts.Low] = 5000,
        [ReasoningEfforts.Medium] = 15000,
        [ReasoningEfforts.High] = 30000,
        [ReasoningEfforts.XHigh] = 30000,
        [ReasoningEfforts.Max] = 30000,
    };

    private static readonly JsonElement EmptyInputSchema = AnthropicJson.RawElement(
        "{\"type\":\"object\",\"properties\":{}}"u8);

    /// <summary>Port of convertToAnthropicRequestWithConfig.</summary>
    public static AnthropicMessageRequest ConvertToAnthropicRequestWithConfig(
        LlmRequest chatReq,
        AnthropicOutboundConfig? config)
    {
        ArgumentNullException.ThrowIfNull(chatReq);

        var req = BuildBaseRequest(chatReq, config);

        var tools = ConvertToolsAnthropic(chatReq.Tools, config);
        req.Tools = tools.Count > 0 ? tools : null;
        req.ToolChoice = ConvertToolChoiceToAnthropic(chatReq.ToolChoice);
        req.ToolChoice = ApplyParallelToolCalls(req.ToolChoice, chatReq.ParallelToolCalls, tools.Count > 0);
        req.Messages = ConvertMessages(chatReq, config);
        req.StopSequences = ConvertStopSequences(chatReq.Stop);

        // DeepSeek requires assistant messages in history to include a thinking
        // block when thinking is enabled (matching their OpenAI API behavior).
        if (config is not null && config.Type == AnthropicPlatformType.DeepSeek && IsThinkingEnabled(req))
        {
            EnsureAssistantThinkingBlocks(req.Messages);
        }

        return req;
    }

    /// <summary>Port of isThinkingEnabled.</summary>
    public static bool IsThinkingEnabled(AnthropicMessageRequest req)
        => req.Thinking is null || req.Thinking.Type != "disabled";

    /// <summary>Port of ensureAssistantThinkingBlocks.</summary>
    public static void EnsureAssistantThinkingBlocks(List<AnthropicMessageParam> messages)
    {
        foreach (var msg in messages)
        {
            if (msg.Role != "assistant" || HasThinkingBlock(msg))
            {
                continue;
            }

            var thinkingBlock = new AnthropicMessageContentBlock
            {
                Type = "thinking",
                Thinking = string.Empty,
            };

            msg.Content ??= new AnthropicMessageContent();
            if (msg.Content.Content is not null)
            {
                // Convert simple string content to multiple content with
                // thinking + text.
                msg.Content = new AnthropicMessageContent
                {
                    MultipleContent =
                    [
                        thinkingBlock,
                        new AnthropicMessageContentBlock
                        {
                            Type = "text",
                            Text = msg.Content.Content,
                        },
                    ],
                };
            }
            else
            {
                // Prepend the thinking block to existing multiple content.
                msg.Content.MultipleContent =
                [
                    thinkingBlock,
                    .. msg.Content.MultipleContent ?? [],
                ];
            }
        }
    }

    /// <summary>Port of hasThinkingBlock.</summary>
    public static bool HasThinkingBlock(AnthropicMessageParam msg)
        => msg.Content?.MultipleContent?.Any(static block => block.Type == "thinking") == true;

    /// <summary>Port of shouldDecodeAnthropicSignature.</summary>
    public static bool ShouldDecodeAnthropicSignature(AnthropicOutboundConfig? config)
    {
        if (config is null)
        {
            return true;
        }

        return config.Type is AnthropicPlatformType.Direct
            or AnthropicPlatformType.ClaudeCode
            or AnthropicPlatformType.Vertex
            or AnthropicPlatformType.Bedrock
            or AnthropicPlatformType.CommandCode;
    }

    /// <summary>Port of prepareAnthropicReasoning.</summary>
    public static (string? Content, string? Signature) PrepareAnthropicReasoning(
        string? reasoningContent,
        string? reasoningSignature,
        AnthropicOutboundConfig? config)
    {
        if (string.IsNullOrEmpty(reasoningSignature))
        {
            return (reasoningContent, reasoningSignature);
        }

        if (ShouldDecodeAnthropicSignature(config))
        {
            var decoded = Shared.AnthropicSignatures.DecodeAnthropicSignature(reasoningSignature);
            if (decoded is not null)
            {
                return (reasoningContent, decoded);
            }

            return (null, null);
        }

        return (reasoningContent, reasoningSignature);
    }

    /// <summary>Port of buildBaseRequest.</summary>
    public static AnthropicMessageRequest BuildBaseRequest(LlmRequest chatReq, AnthropicOutboundConfig? config)
    {
        ArgumentNullException.ThrowIfNull(chatReq);

        var req = new AnthropicMessageRequest
        {
            Model = chatReq.Model,
            Temperature = chatReq.Temperature,
            TopP = chatReq.TopP,
            Stream = chatReq.Stream,
            System = ConvertToAnthropicSystemPrompt(chatReq),
            MaxTokens = ResolveMaxTokens(chatReq),
        };

        if (chatReq.Metadata is not null
            && chatReq.Metadata.TryGetValue("user_id", out var userId)
            && !string.IsNullOrEmpty(userId))
        {
            req.Metadata = new AnthropicMetadata { UserId = userId };
        }

        // Native Anthropic thinking markers recorded by the inbound transformer.
        var thinkingType = string.Empty;
        var nativeEffort = string.Empty;
        if (chatReq.TransformerMetadata is not null)
        {
            thinkingType = GetMetaString(chatReq.TransformerMetadata, AnthropicJson.KeyThinkingType);
            nativeEffort = GetMetaString(chatReq.TransformerMetadata, AnthropicJson.KeyOutputConfigEffort);
        }

        // DeepSeek's Anthropic format supports output_config.effort. When
        // reasoning_effort is present, prefer output_config over thinking so
        // suffix-based effort routing preserves the explicit level.
        if (config is not null
            && config.Type == AnthropicPlatformType.DeepSeek
            && !string.IsNullOrEmpty(chatReq.ReasoningEffort)
            && chatReq.ReasoningEffort != ReasoningEfforts.None)
        {
            req.OutputConfig = new AnthropicOutputConfig
            {
                Effort = NormalizeAnthropicEffort(chatReq.ReasoningEffort),
            };
        }

        // Determine thinking config priority: disabled > adaptive > native
        // budget (enabled).
        switch (thinkingType)
        {
            case "disabled":
                req.Thinking = new AnthropicThinking { Type = "disabled" };
                break;
            case "adaptive":
                req.Thinking = new AnthropicThinking { Type = "adaptive" };
                break;
            case "between_tools":
                req.Thinking = new AnthropicThinking { Type = "between_tools" };
                break;
            case "enabled":
                // Native budget client: round-trip budget_tokens verbatim.
                // DeepSeek's effort representation is output_config.effort;
                // sending both configurations is rejected upstream.
                if (req.OutputConfig is null)
                {
                    req.Thinking = BuildThinking(chatReq, config);
                }

                break;
        }

        // ReasoningEffort="none" means disabled thinking (e.g. from the OpenAI
        // inbound transformer) even when no metadata marker is present.
        if (req.Thinking is null && chatReq.ReasoningEffort == ReasoningEfforts.None)
        {
            req.Thinking = new AnthropicThinking { Type = "disabled" };
        }

        // Converted request carrying an explicit effort level: express it via
        // output_config.effort (plus adaptive thinking) instead of converting
        // the level into a budget.
        if (req.OutputConfig is null
            && req.Thinking is null
            && nativeEffort.Length == 0
            && !string.IsNullOrEmpty(chatReq.ReasoningEffort)
            && chatReq.ReasoningEffort != ReasoningEfforts.None)
        {
            if (SupportsOutputConfig(config))
            {
                req.OutputConfig = new AnthropicOutputConfig
                {
                    Effort = NormalizeAnthropicEffort(chatReq.ReasoningEffort),
                };

                if (SupportsAdaptiveThinking(config))
                {
                    req.Thinking = new AnthropicThinking { Type = "adaptive" };
                }
            }
            else
            {
                req.Thinking = BuildThinking(chatReq, config);
            }
        }

        // Converted request without an effort level but with an explicit
        // budget: pass the budget through verbatim.
        if (req.OutputConfig is null && req.Thinking is null && chatReq.ReasoningBudget is not null)
        {
            req.Thinking = BuildThinking(chatReq, config);
        }

        // Restore thinking display from TransformerMetadata.
        if (req.Thinking is not null && chatReq.TransformerMetadata is not null)
        {
            var display = GetMetaString(chatReq.TransformerMetadata, AnthropicJson.KeyThinkingDisplay);
            if (display.Length > 0)
            {
                req.Thinking.Display = display;
            }
        }

        // Restore output_config from TransformerMetadata.
        if (chatReq.TransformerMetadata is not null)
        {
            var effort = GetMetaString(chatReq.TransformerMetadata, AnthropicJson.KeyOutputConfigEffort);
            if (effort.Length > 0 && effort != ReasoningEfforts.None)
            {
                if (SupportsOutputConfig(config))
                {
                    req.OutputConfig = new AnthropicOutputConfig { Effort = effort };
                }
                else if (req.Thinking is null || req.Thinking.Type == "adaptive")
                {
                    req.Thinking = new AnthropicThinking
                    {
                        Type = "enabled",
                        BudgetTokens = GetThinkingBudgetTokensWithConfig(effort, config),
                    };
                }
            }
        }

        // Restore Anthropic's top-level cache_control (automatic prompt
        // caching); TransformRequest then skips the breakpoint optimizer.
        if (chatReq.TransformerMetadata is not null)
        {
            var cacheControl = GetMetaCacheControl(chatReq.TransformerMetadata);
            if (cacheControl is not null)
            {
                req.CacheControl = cacheControl;
            }
        }

        return req;
    }

    /// <summary>Port of resolveMaxTokens.</summary>
    public static long ResolveMaxTokens(LlmRequest chatReq)
    {
        ArgumentNullException.ThrowIfNull(chatReq);

        if (chatReq.MaxTokens is { } maxTokens)
        {
            return maxTokens;
        }

        if (chatReq.MaxCompletionTokens is { } maxCompletionTokens)
        {
            return maxCompletionTokens;
        }

        if (chatReq.TransformOptions?.DefaultMaxTokens is { } defaultMaxTokens && defaultMaxTokens > 0)
        {
            return defaultMaxTokens;
        }

        return DefaultAnthropicMaxTokens;
    }

    /// <summary>Port of buildThinking.</summary>
    public static AnthropicThinking BuildThinking(LlmRequest chatReq, AnthropicOutboundConfig? config)
    {
        ArgumentNullException.ThrowIfNull(chatReq);

        var budgetTokens = chatReq.ReasoningBudget
            ?? GetThinkingBudgetTokensWithConfig(chatReq.ReasoningEffort, config);

        return new AnthropicThinking
        {
            Type = "enabled",
            // Go's int64 omitempty drops a zero budget from the wire.
            BudgetTokens = budgetTokens == 0 ? null : budgetTokens,
        };
    }

    /// <summary>Port of convertToolsAnthropic.</summary>
    public static List<AnthropicTool> ConvertToolsAnthropic(List<Tool>? tools, AnthropicOutboundConfig? config)
    {
        var anthropicTools = new List<AnthropicTool>(tools?.Count ?? 0);
        if (tools is null || tools.Count == 0)
        {
            return anthropicTools;
        }

        var supportsNativeTools = SupportsAnthropicNativeTools(config);

        foreach (var tool in tools)
        {
            switch (tool.Type)
            {
                case ToolTypes.Function:
                    anthropicTools.Add(new AnthropicTool
                    {
                        Name = tool.Function.Name,
                        Description = EmptyToNull(tool.Function.Description),
                        InputSchema = NormalizeInputSchema(tool.Function.Parameters),
                        Strict = tool.Function.Strict,
                        CacheControl = ConvertToAnthropicCacheControl(tool.CacheControl),
                    });
                    break;
                case ToolTypes.WebSearch:
                    // Already transformed Anthropic native tool type; skip it
                    // on platforms without native tool support.
                    if (!supportsNativeTools)
                    {
                        continue;
                    }

                    anthropicTools.Add(BuildWebSearchTool(tool));
                    break;
                default:
                    // Ignore other native tools (image_generation, google_*, ...).
                    continue;
            }
        }

        return anthropicTools;
    }

    private static AnthropicTool BuildWebSearchTool(Tool tool)
    {
        var anthropicTool = new AnthropicTool
        {
            Type = ToolTypeWebSearch20250305,
            Name = WebSearchFunctionName,
        };

        if (tool.WebSearch is { } webSearch)
        {
            anthropicTool.MaxUses = webSearch.MaxUses;
            anthropicTool.Strict = webSearch.Strict;
            anthropicTool.AllowedDomains = EmptyToNull(webSearch.AllowedDomains);
            anthropicTool.BlockedDomains = EmptyToNull(webSearch.BlockedDomains);
            anthropicTool.UserLocation = ConvertWebSearchUserLocation(webSearch.UserLocation);
        }

        return anthropicTool;
    }

    /// <summary>
    /// Go's user_location is a value struct with <c>omitzero</c>: a zero value
    /// disappears from the wire.
    /// </summary>
    private static AnthropicWebSearchToolUserLocation? ConvertWebSearchUserLocation(
        WebSearchToolUserLocation? location)
    {
        var city = location?.City ?? string.Empty;
        var country = location?.Country ?? string.Empty;
        var region = location?.Region ?? string.Empty;
        var timezone = location?.Timezone ?? string.Empty;
        var type = location?.Type ?? string.Empty;

        if (city.Length == 0 && country.Length == 0 && region.Length == 0 && timezone.Length == 0 && type.Length == 0)
        {
            return null;
        }

        return new AnthropicWebSearchToolUserLocation
        {
            City = EmptyToNull(city),
            Country = EmptyToNull(country),
            Region = EmptyToNull(region),
            Timezone = EmptyToNull(timezone),
            Type = type,
        };
    }

    /// <summary>Port of convertToolChoiceToAnthropic.</summary>
    public static AnthropicToolChoice? ConvertToolChoiceToAnthropic(ToolChoice? source)
    {
        if (source is null)
        {
            return null;
        }

        // String-form tool_choice: "auto", "none", "any", "required".
        if (source.ToolChoiceMode is { } choice)
        {
            // OpenAI "required" is equivalent to Anthropic "any".
            if (choice == "required")
            {
                choice = "any";
            }

            return new AnthropicToolChoice { Type = choice };
        }

        // Named tool_choice: {type: "function", function: {name: "xxx"}}.
        if (source.NamedToolChoice is { Function.Name: { Length: > 0 } name })
        {
            return new AnthropicToolChoice { Type = "tool", Name = name };
        }

        return null;
    }

    /// <summary>Port of applyParallelToolCalls.</summary>
    public static AnthropicToolChoice? ApplyParallelToolCalls(
        AnthropicToolChoice? choice,
        bool? parallelToolCalls,
        bool hasTools)
    {
        if (parallelToolCalls is null || !hasTools)
        {
            return choice;
        }

        if (choice is null)
        {
            if (parallelToolCalls.Value)
            {
                return null;
            }

            choice = new AnthropicToolChoice { Type = "auto" };
        }

        // "none" has no disable_parallel_tool_use field.
        if (choice.Type == "none")
        {
            return choice;
        }

        choice.DisableParallelToolUse = !parallelToolCalls.Value;

        return choice;
    }

    /// <summary>Port of convertStopSequences.</summary>
    public static List<string>? ConvertStopSequences(Stop? stop)
    {
        if (stop is null)
        {
            return null;
        }

        if (stop.Single is not null)
        {
            return [stop.Single];
        }

        return stop.Multiple is { Count: > 0 } ? [.. stop.Multiple] : null;
    }

    /// <summary>Port of validateUnsupportedContentParts.</summary>
    public static void ValidateUnsupportedContentParts(List<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var message in messages)
        {
            if (message.Content?.MultipleContent is not { } parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part.Type == "input_audio")
                {
                    throw TransformerErrors.InvalidRequest(
                        "input_audio content parts are not supported by the Anthropic Messages API");
                }
            }
        }
    }

    /// <summary>Port of validateDroppedContentParts.</summary>
    public static void ValidateDroppedContentParts(List<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role != "user"
                || message.Content?.Content is not null
                || message.Content?.MultipleContent is not { Count: > 0 } parts
                || HasThinkingContent(message))
            {
                continue;
            }

            // Reuse the converter so this check cannot drift from what it
            // accepts.
            var content = ConvertMultiplePartContent(message);
            if (content.Ok
                && (content.Content.Content is not null
                    || content.Content.MultipleContent is { Count: > 0 }
                    || content.Content.Raw is { Length: > 0 }))
            {
                continue;
            }

            var dropped = new List<string>(parts.Count);
            foreach (var part in parts)
            {
                dropped.Add(part.Type is "text" or "image_url"
                    ? part.Type + " without payload"
                    : part.Type);
            }

            throw TransformerErrors.InvalidRequest(
                "message " + i + " (role \"user\") cannot be represented in the Anthropic Messages API: "
                + "only text and image_url content parts are supported, "
                + string.Join(", ", Distinct(dropped)) + " would be dropped");
        }
    }

    /// <summary>Port of convertMessages.</summary>
    public static List<AnthropicMessageParam> ConvertMessages(LlmRequest chatReq, AnthropicOutboundConfig? config)
    {
        ArgumentNullException.ThrowIfNull(chatReq);

        // System and developer messages are handled separately.
        var nonSystemMsgs = chatReq.Messages
            .Where(static msg => msg.Role != "system" && msg.Role != "developer")
            .ToList();

        var messages = new List<AnthropicMessageParam>(nonSystemMsgs.Count);

        // Track which message indexes have been processed (for user messages
        // with MessageIndex and tool messages).
        var processedMessageIndexes = new Dictionary<int, bool>();
        var processedToolCallIds = new Dictionary<string, bool>(StringComparer.Ordinal);

        for (var i = 0; i < nonSystemMsgs.Count; i++)
        {
            var msg = nonSystemMsgs[i];

            if (processedMessageIndexes.ContainsKey(i))
            {
                continue;
            }

            switch (msg.Role)
            {
                case "tool":
                    // Standalone tool messages: group consecutive ones into a
                    // single user message with tool_results.
                    var grouped = GroupToolResultMessages(
                        nonSystemMsgs,
                        i,
                        processedMessageIndexes,
                        processedToolCallIds);
                    if (grouped.Created)
                    {
                        messages.Add(grouped.Message);
                        i = grouped.NewIndex;
                    }

                    break;

                case "user":
                    // Skip user messages already merged with tool results.
                    if (msg.MessageIndex is { } messageIndex
                        && processedMessageIndexes.ContainsKey(messageIndex))
                    {
                        continue;
                    }

                    if (ConvertUserMessage(msg) is { } userMessage)
                    {
                        messages.Add(userMessage);
                    }

                    break;

                case "assistant":
                    if (ConvertAssistantMessage(msg, config) is { } assistantMessage)
                    {
                        messages.Add(assistantMessage);
                    }

                    // After an assistant message with tool calls, the next
                    // message may carry the tool results.
                    if (msg.ToolCalls is { Count: > 0 } toolCalls)
                    {
                        var found = FindToolResultsForAssistant(
                            nonSystemMsgs,
                            toolCalls,
                            processedToolCallIds,
                            processedMessageIndexes);
                        if (found.Found)
                        {
                            messages.Add(found.Message);
                        }
                        else if (i + 1 < nonSystemMsgs.Count)
                        {
                            // Legacy fallback: group consecutive tool messages.
                            var fallback = GroupToolResultMessages(
                                nonSystemMsgs,
                                i + 1,
                                processedMessageIndexes,
                                processedToolCallIds);
                            if (fallback.Created)
                            {
                                messages.Add(fallback.Message);
                                i = fallback.NewIndex;
                            }
                        }
                    }

                    break;
            }
        }

        return messages;
    }

    /// <summary>Port of findToolResultsForAssistant.</summary>
    public static (AnthropicMessageParam Message, bool Found) FindToolResultsForAssistant(
        List<Message> messages,
        List<ToolCall> toolCalls,
        Dictionary<string, bool> processedToolCallIds,
        Dictionary<int, bool> processedMessageIndexes)
    {
        var toolResultBlocks = new List<AnthropicMessageContentBlock>();
        var toolMessageIndexes = new HashSet<int>();

        foreach (var toolCall in toolCalls)
        {
            // Go reads a string map, so a missing id behaves like "".
            var toolCallIdValue = toolCall.Id ?? string.Empty;

            if (processedToolCallIds.ContainsKey(toolCallIdValue))
            {
                continue;
            }

            // Look for this tool call ID in all messages.
            for (var i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                if (msg.Role == "tool"
                    && msg.ToolCallId is { } toolCallId
                    && toolCallId == toolCallIdValue)
                {
                    toolResultBlocks.Add(ConvertToToolResultBlock(msg));
                    processedToolCallIds[toolCallIdValue] = true;

                    processedMessageIndexes[i] = true;

                    if (msg.MessageIndex is { } messageIndex)
                    {
                        toolMessageIndexes.Add(messageIndex);
                    }

                    break;
                }
            }
        }

        // Look for a related user message with the same MessageIndex.
        if (toolMessageIndexes.Count > 0)
        {
            for (var j = 0; j < messages.Count; j++)
            {
                if (processedMessageIndexes.ContainsKey(j))
                {
                    continue;
                }

                var userMessage = messages[j];
                if (userMessage.Role == "user"
                    && userMessage.MessageIndex is { } userMessageIndex
                    && toolMessageIndexes.Contains(userMessageIndex))
                {
                    toolResultBlocks.AddRange(ExtractUserContentBlocks(userMessage));
                    processedMessageIndexes[j] = true;
                }
            }
        }

        if (toolResultBlocks.Count > 0)
        {
            return (
                new AnthropicMessageParam
                {
                    Role = "user",
                    Content = new AnthropicMessageContent { MultipleContent = toolResultBlocks },
                },
                true);
        }

        return (new AnthropicMessageParam(), false);
    }

    /// <summary>Port of groupToolResultMessages.</summary>
    public static (AnthropicMessageParam Message, int NewIndex, bool Created) GroupToolResultMessages(
        List<Message> messages,
        int startIndex,
        Dictionary<int, bool> processedIndexes,
        Dictionary<string, bool> processedIds)
    {
        var toolResultBlocks = new List<AnthropicMessageContentBlock>();
        var toolMessageIndexes = new HashSet<int>();
        var currentIndex = startIndex;

        // Group consecutive tool messages.
        while (currentIndex < messages.Count && messages[currentIndex].Role == "tool")
        {
            var toolMessage = messages[currentIndex];
            if (toolMessage.ToolCallId is { } toolCallId && processedIds.ContainsKey(toolCallId))
            {
                currentIndex++;
                continue;
            }

            toolResultBlocks.Add(ConvertToToolResultBlock(toolMessage));
            if (toolMessage.ToolCallId is { } id)
            {
                processedIds[id] = true;
            }

            if (toolMessage.MessageIndex is { } messageIndex)
            {
                toolMessageIndexes.Add(messageIndex);
            }

            processedIndexes[currentIndex] = true;
            currentIndex++;
        }

        // Look for a related user message with the same MessageIndex.
        if (toolMessageIndexes.Count > 0)
        {
            for (var j = 0; j < messages.Count; j++)
            {
                if (processedIndexes.ContainsKey(j))
                {
                    continue;
                }

                var userMessage = messages[j];
                if (userMessage.Role == "user"
                    && userMessage.MessageIndex is { } userMessageIndex
                    && toolMessageIndexes.Contains(userMessageIndex))
                {
                    toolResultBlocks.AddRange(ExtractUserContentBlocks(userMessage));
                    processedIndexes[j] = true;
                }
            }
        }

        if (toolResultBlocks.Count > 0)
        {
            return (
                new AnthropicMessageParam
                {
                    Role = "user",
                    Content = new AnthropicMessageContent { MultipleContent = toolResultBlocks },
                },
                currentIndex - 1,
                true);
        }

        return (new AnthropicMessageParam(), startIndex, false);
    }

    /// <summary>Port of extractUserContentBlocks.</summary>
    public static List<AnthropicMessageContentBlock> ExtractUserContentBlocks(Message msg)
    {
        var blocks = new List<AnthropicMessageContentBlock>();

        if (msg.Content?.Content is { Length: > 0 } content)
        {
            blocks.Add(new AnthropicMessageContentBlock
            {
                Type = "text",
                Text = content,
                CacheControl = ConvertToAnthropicCacheControl(msg.CacheControl),
            });
        }
        else if (msg.Content?.MultipleContent is { Count: > 0 } parts)
        {
            foreach (var part in parts)
            {
                if (part.Type == "text" && part.Text is { } text)
                {
                    blocks.Add(new AnthropicMessageContentBlock
                    {
                        Type = "text",
                        Text = text,
                        CacheControl = ConvertToAnthropicCacheControl(part.CacheControl),
                    });
                }
            }
        }

        return blocks;
    }

    /// <summary>Port of convertUserMessage.</summary>
    public static AnthropicMessageParam? ConvertUserMessage(Message msg)
    {
        var content = BuildMessageContent(msg, null);
        if (!content.Ok)
        {
            return null;
        }

        return new AnthropicMessageParam { Role = "user", Content = content.Content };
    }

    /// <summary>Port of convertAssistantMessage.</summary>
    public static AnthropicMessageParam? ConvertAssistantMessage(Message msg, AnthropicOutboundConfig? config)
        => ConvertAssistantWithToolCalls(msg, config);

    /// <summary>Port of convertAssistantWithToolCalls.</summary>
    public static AnthropicMessageParam? ConvertAssistantWithToolCalls(
        Message msg,
        AnthropicOutboundConfig? config)
    {
        var preBlocks = BuildPreBlocks(msg, config);
        var toolContent = ConvertMultiplePartContent(msg);

        AnthropicMessageContent content;
        if (toolContent.Ok && preBlocks.Count > 0)
        {
            content = toolContent.Content;
            content.MultipleContent = [.. preBlocks, .. content.MultipleContent ?? []];
        }
        else if (toolContent.Ok)
        {
            content = toolContent.Content;
        }
        else if (preBlocks.Count > 0)
        {
            content = BuildContentFromBlocks(preBlocks);
        }
        else
        {
            return null;
        }

        return new AnthropicMessageParam { Role = "assistant", Content = content };
    }

    /// <summary>Port of buildPreBlocks.</summary>
    public static List<AnthropicMessageContentBlock> BuildPreBlocks(
        Message msg,
        AnthropicOutboundConfig? config)
    {
        var blocks = new List<AnthropicMessageContentBlock>();

        blocks.AddRange(BuildThinkingBlocks(msg, config));

        if (BuildRedactedThinkingBlock(msg.RedactedReasoningContent) is { } redacted)
        {
            blocks.Add(redacted);
        }

        if (msg.Content?.Content is { Length: > 0 } content)
        {
            blocks.Add(new AnthropicMessageContentBlock
            {
                Type = "text",
                Text = content,
                CacheControl = ConvertToAnthropicCacheControl(msg.CacheControl),
            });
        }

        return blocks;
    }

    /// <summary>Port of buildContentFromBlocks.</summary>
    public static AnthropicMessageContent BuildContentFromBlocks(List<AnthropicMessageContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        if (blocks.Count == 1 && blocks[0].Type == "text")
        {
            return new AnthropicMessageContent { Content = blocks[0].Text };
        }

        return new AnthropicMessageContent { MultipleContent = blocks };
    }

    /// <summary>Port of buildMessageContent.</summary>
    public static (AnthropicMessageContent Content, bool Ok) BuildMessageContent(
        Message msg,
        AnthropicOutboundConfig? config)
    {
        // Handle simple string content.
        if (msg.Content?.Content is { } content)
        {
            if (msg.CacheControl is not null || HasThinkingContent(msg))
            {
                return (BuildMultipleContentWithThinking(msg, config), true);
            }

            return (new AnthropicMessageContent { Content = content }, true);
        }

        var blocks = new List<AnthropicMessageContentBlock>();

        if (HasThinkingContent(msg))
        {
            blocks.AddRange(BuildThinkingBlocks(msg, config));

            if (BuildRedactedThinkingBlock(msg.RedactedReasoningContent) is { } redacted)
            {
                blocks.Add(redacted);
            }
        }

        var partContent = ConvertMultiplePartContent(msg);

        if (partContent.Ok && blocks.Count > 0)
        {
            // Matches the reference: a message that carries both reasoning and
            // multipart content is dropped rather than interleaved.
            return (new AnthropicMessageContent(), false);
        }

        if (blocks.Count > 0)
        {
            return (
                new AnthropicMessageContent
                {
                    MultipleContent = [.. blocks, .. partContent.Content.MultipleContent ?? []],
                },
                true);
        }

        return (partContent.Content, true);
    }

    /// <summary>Port of hasThinkingContent.</summary>
    public static bool HasThinkingContent(Message msg)
        => msg.ReasoningItems is { Count: > 0 }
           || !string.IsNullOrEmpty(msg.ReasoningContent)
           || !string.IsNullOrEmpty(msg.ReasoningSignature)
           || !string.IsNullOrEmpty(msg.RedactedReasoningContent);

    /// <summary>Port of buildMultipleContentWithThinking.</summary>
    public static AnthropicMessageContent BuildMultipleContentWithThinking(
        Message msg,
        AnthropicOutboundConfig? config)
    {
        var blocks = new List<AnthropicMessageContentBlock>(3);

        blocks.AddRange(BuildThinkingBlocks(msg, config));

        if (BuildRedactedThinkingBlock(msg.RedactedReasoningContent) is { } redacted)
        {
            blocks.Add(redacted);
        }

        blocks.Add(new AnthropicMessageContentBlock
        {
            Type = "text",
            Text = msg.Content?.Content,
            CacheControl = ConvertToAnthropicCacheControl(msg.CacheControl),
        });

        return new AnthropicMessageContent { MultipleContent = blocks };
    }

    /// <summary>Port of buildThinkingBlocks.</summary>
    public static List<AnthropicMessageContentBlock> BuildThinkingBlocks(
        Message msg,
        AnthropicOutboundConfig? config)
    {
        var reasoningItems = msg.ReasoningItems;
        if (reasoningItems is not { Count: > 0 })
        {
            reasoningItems =
            [
                new ReasoningItem
                {
                    Content = msg.ReasoningContent,
                    Signature = msg.ReasoningSignature,
                },
            ];
        }

        var blocks = new List<AnthropicMessageContentBlock>(reasoningItems.Count);
        foreach (var reasoningItem in reasoningItems)
        {
            var reasoningContent = (string?)reasoningItem.Content;
            string? reasoningSignature = string.IsNullOrEmpty(reasoningItem.Signature)
                ? null
                : reasoningItem.Signature;

            (reasoningContent, reasoningSignature) = PrepareAnthropicReasoning(
                reasoningContent,
                reasoningSignature,
                config);

            if (BuildThinkingBlock(reasoningContent, reasoningSignature) is { } block)
            {
                blocks.Add(block);
            }
        }

        return blocks;
    }

    /// <summary>Port of buildThinkingBlock.</summary>
    public static AnthropicMessageContentBlock? BuildThinkingBlock(
        string? reasoningContent,
        string? reasoningSignature)
    {
        if (string.IsNullOrEmpty(reasoningContent) && string.IsNullOrEmpty(reasoningSignature))
        {
            return null;
        }

        return new AnthropicMessageContentBlock
        {
            Type = "thinking",
            Thinking = reasoningContent ?? string.Empty,
            Signature = reasoningSignature,
        };
    }

    /// <summary>Port of buildRedactedThinkingBlock.</summary>
    public static AnthropicMessageContentBlock? BuildRedactedThinkingBlock(string? redactedContent)
        => string.IsNullOrEmpty(redactedContent)
            ? null
            : new AnthropicMessageContentBlock
            {
                Type = "redacted_thinking",
                Data = redactedContent,
            };

    /// <summary>Port of convertToToolResultBlock.</summary>
    public static AnthropicMessageContentBlock ConvertToToolResultBlock(Message msg)
    {
        ArgumentNullException.ThrowIfNull(msg);

        return new AnthropicMessageContentBlock
        {
            Type = "tool_result",
            ToolUseId = msg.ToolCallId,
            Content = ConvertToAnthropicTrivialContent(msg.Content),
            CacheControl = ConvertToAnthropicCacheControl(msg.CacheControl),
            IsError = msg.ToolCallIsError,
        };
    }

    /// <summary>Port of convertImageURLToAnthropicBlock.</summary>
    public static bool ConvertImageUrlToAnthropicBlock(
        MessageContentPart part,
        out AnthropicMessageContentBlock block)
    {
        block = new AnthropicMessageContentBlock();

        if (part.ImageUrl is not { Url.Length: > 0 } imageUrl)
        {
            return false;
        }

        var cacheControl = ConvertToAnthropicCacheControl(part.CacheControl);
        if (AnthropicJson.ParseDataUrl(imageUrl.Url) is { } parsed)
        {
            block = new AnthropicMessageContentBlock
            {
                Type = "image",
                Source = new AnthropicImageSource
                {
                    Type = "base64",
                    MediaType = parsed.MediaType,
                    Data = parsed.Data,
                },
                CacheControl = cacheControl,
            };
            return true;
        }

        block = new AnthropicMessageContentBlock
        {
            Type = "image",
            Source = new AnthropicImageSource
            {
                Type = "url",
                Url = imageUrl.Url,
            },
            CacheControl = cacheControl,
        };
        return true;
    }

    /// <summary>Port of convertToAnthropicTrivialContent.</summary>
    public static AnthropicMessageContent? ConvertToAnthropicTrivialContent(MessageContent? content)
    {
        if (content is null)
        {
            return null;
        }

        if (content.Content is not null)
        {
            return new AnthropicMessageContent { Content = content.Content };
        }

        if (content.MultipleContent is { Count: > 0 } parts)
        {
            var blocks = new List<AnthropicMessageContentBlock>(parts.Count);
            foreach (var part in parts)
            {
                switch (part.Type)
                {
                    case "text":
                        if (part.Text is { } text)
                        {
                            blocks.Add(new AnthropicMessageContentBlock
                            {
                                Type = "text",
                                Text = text,
                                CacheControl = ConvertToAnthropicCacheControl(part.CacheControl),
                            });
                        }

                        break;
                    case "image_url":
                        if (ConvertImageUrlToAnthropicBlock(part, out var imageBlock))
                        {
                            blocks.Add(imageBlock);
                        }

                        break;
                }
            }

            // Go returns a non-nil empty slice, which marshals as [].
            return new AnthropicMessageContent { MultipleContent = blocks };
        }

        return null;
    }

    /// <summary>Port of systemMessageToParts.</summary>
    public static List<AnthropicSystemPromptPart> SystemMessageToParts(Message msg)
    {
        ArgumentNullException.ThrowIfNull(msg);

        if (msg.Content?.Content is { } content)
        {
            return
            [
                new AnthropicSystemPromptPart
                {
                    Type = "text",
                    Text = content,
                    CacheControl = ConvertToAnthropicCacheControl(msg.CacheControl),
                },
            ];
        }

        var parts = new List<AnthropicSystemPromptPart>(msg.Content?.MultipleContent?.Count ?? 0);
        if (msg.Content?.MultipleContent is { } contentParts)
        {
            foreach (var part in contentParts)
            {
                if (part.Type == "text" && part.Text is { } text)
                {
                    parts.Add(new AnthropicSystemPromptPart
                    {
                        Type = "text",
                        Text = text,
                        CacheControl = ConvertToAnthropicCacheControl(part.CacheControl),
                    });
                }
            }
        }

        return parts;
    }

    /// <summary>Port of convertToAnthropicSystemPrompt.</summary>
    public static AnthropicSystemPrompt? ConvertToAnthropicSystemPrompt(LlmRequest chatReq)
    {
        ArgumentNullException.ThrowIfNull(chatReq);

        // Partition into system and developer roles; the reference appends the
        // developer messages after every system message.
        var systemOnlyMessages = new List<Message>();
        var developerMessages = new List<Message>();

        foreach (var msg in chatReq.Messages)
        {
            switch (msg.Role)
            {
                case "system":
                    systemOnlyMessages.Add(msg);
                    break;
                case "developer":
                    developerMessages.Add(msg);
                    break;
            }
        }

        var systemMessages = new List<Message>(systemOnlyMessages.Count + developerMessages.Count);
        systemMessages.AddRange(systemOnlyMessages);
        systemMessages.AddRange(developerMessages);

        // Check if system was originally in array format.
        var wasArrayFormat = chatReq.TransformOptions?.ArrayInstructions == true;

        switch (systemMessages.Count)
        {
            case 0:
                return null;
            case 1:
                var parts = SystemMessageToParts(systemMessages[0]);

                // If it was originally in array format, preserve that format.
                if (wasArrayFormat)
                {
                    return new AnthropicSystemPrompt { MultiplePrompts = parts };
                }

                if (parts.Count == 1)
                {
                    return new AnthropicSystemPrompt { Prompt = parts[0].Text };
                }

                return new AnthropicSystemPrompt { MultiplePrompts = parts };
            default:
                var combined = new List<AnthropicSystemPromptPart>();
                foreach (var message in systemMessages)
                {
                    combined.AddRange(SystemMessageToParts(message));
                }

                return new AnthropicSystemPrompt { MultiplePrompts = combined };
        }
    }

    /// <summary>Port of convertMultiplePartContent.</summary>
    public static (AnthropicMessageContent Content, bool Ok) ConvertMultiplePartContent(Message msg)
    {
        ArgumentNullException.ThrowIfNull(msg);

        var ordered = new List<AnthropicConvertFromLlm.OrderedContentBlock>();

        void AppendOrdered(Dictionary<string, JsonElement>? metadata, AnthropicMessageContentBlock block)
        {
            ordered.Add(new AnthropicConvertFromLlm.OrderedContentBlock(
                AnthropicJson.GetBlockIndex(metadata),
                ordered.Count,
                block));
        }

        // Process content parts in order to preserve the original sequence.
        foreach (var part in msg.Content?.MultipleContent ?? [])
        {
            switch (part.Type)
            {
                case "text":
                    if (part.Text is { } text)
                    {
                        AppendOrdered(part.TransformerMetadata, new AnthropicMessageContentBlock
                        {
                            Type = "text",
                            Text = text,
                            CacheControl = ConvertToAnthropicCacheControl(part.CacheControl),
                        });
                    }

                    break;
                case "image_url":
                    if (part.ImageUrl is { Url.Length: > 0 } imageUrl)
                    {
                        var cacheControl = ConvertToAnthropicCacheControl(part.CacheControl);
                        if (AnthropicJson.ParseDataUrl(imageUrl.Url) is { } parsed)
                        {
                            AppendOrdered(part.TransformerMetadata, new AnthropicMessageContentBlock
                            {
                                Type = "image",
                                Source = new AnthropicImageSource
                                {
                                    Type = "base64",
                                    MediaType = parsed.MediaType,
                                    Data = parsed.Data,
                                },
                                CacheControl = cacheControl,
                            });
                        }
                        else
                        {
                            AppendOrdered(part.TransformerMetadata, new AnthropicMessageContentBlock
                            {
                                Type = "image",
                                Source = new AnthropicImageSource
                                {
                                    Type = "url",
                                    Url = imageUrl.Url,
                                },
                                CacheControl = cacheControl,
                            });
                        }
                    }

                    break;
            }
        }

        foreach (var toolCall in msg.ToolCalls ?? [])
        {
            AppendOrdered(toolCall.TransformerMetadata, ToolUseBlockFromLlm(toolCall));
        }

        foreach (var inlineResult in msg.InlineToolResults ?? [])
        {
            if (AnthropicConvertFromLlm.ToolResultBlockFromInline(inlineResult, out var block))
            {
                AppendOrdered(inlineResult.TransformerMetadata, block);
            }
        }

        var sorted = AnthropicConvertFromLlm.SortOrderedContentBlocks(ordered);

        if (sorted.Count == 0)
        {
            return (new AnthropicMessageContent(), false);
        }

        return (new AnthropicMessageContent { MultipleContent = sorted }, true);
    }

    /// <summary>Port of toolUseBlockFromLLM.</summary>
    public static AnthropicMessageContentBlock ToolUseBlockFromLlm(ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var blockType = AnthropicJson.GetAnthropicType(toolCall.TransformerMetadata);
        if (blockType.Length == 0)
        {
            blockType = "tool_use";
        }

        return new AnthropicMessageContentBlock
        {
            Type = blockType,
            Id = toolCall.Id,
            Name = toolCall.Function.Name,
            Input = AnthropicJson.RawElement(
                AnthropicJson.SafeJsonRawMessage(toolCall.Function.Arguments)),
            CacheControl = ConvertToAnthropicCacheControl(toolCall.CacheControl),
            Caller = AnthropicJson.GetAnthropicCaller(toolCall.TransformerMetadata),
        };
    }

    /// <summary>Port of containsNativeWebSearchTool.</summary>
    public static bool ContainsNativeWebSearchTool(List<AnthropicTool>? tools)
        => tools?.Any(static tool => tool.Type == ToolTypeWebSearch20250305) == true;

    /// <summary>Port of supportsAnthropicNativeTools.</summary>
    public static bool SupportsAnthropicNativeTools(AnthropicOutboundConfig? config)
    {
        if (config is null)
        {
            return true;
        }

        return config.Type is AnthropicPlatformType.Direct
            or AnthropicPlatformType.Bedrock
            or AnthropicPlatformType.ClaudeCode
            or AnthropicPlatformType.DeepSeek;
    }

    /// <summary>Port of supportsAdaptiveThinking.</summary>
    public static bool SupportsAdaptiveThinking(AnthropicOutboundConfig? config)
    {
        if (config is null)
        {
            return true;
        }

        return config.Type is AnthropicPlatformType.Direct
            or AnthropicPlatformType.ClaudeCode
            or AnthropicPlatformType.Bedrock
            or AnthropicPlatformType.Vertex
            or AnthropicPlatformType.CommandCode;
    }

    /// <summary>Port of supportsOutputConfig.</summary>
    public static bool SupportsOutputConfig(AnthropicOutboundConfig? config)
    {
        if (config is null)
        {
            return true;
        }

        return config.Type is AnthropicPlatformType.Direct
            or AnthropicPlatformType.ClaudeCode
            or AnthropicPlatformType.Bedrock
            or AnthropicPlatformType.Vertex
            or AnthropicPlatformType.DeepSeek
            or AnthropicPlatformType.CommandCode;
    }

    /// <summary>Port of normalizeAnthropicEffort.</summary>
    public static string NormalizeAnthropicEffort(string effort)
        => effort == ReasoningEfforts.Minimal ? ReasoningEfforts.Low : effort;

    /// <summary>Port of getThinkingBudgetTokensWithConfig.</summary>
    public static long GetThinkingBudgetTokensWithConfig(string? reasoningEffort, AnthropicOutboundConfig? config)
    {
        if (config?.ReasoningEffortToBudget is { } mapping
            && reasoningEffort is not null
            && mapping.TryGetValue(reasoningEffort, out var configured))
        {
            return configured;
        }

        if (reasoningEffort is not null
            && DefaultReasoningEffortMapping.TryGetValue(reasoningEffort, out var budget))
        {
            return budget;
        }

        // Default to medium if not found.
        return 15000;
    }

    /// <summary>Port of convertToAnthropicCacheControl (cache_control.go).</summary>
    public static AnthropicCacheControl? ConvertToAnthropicCacheControl(CacheControl? cacheControl)
        => cacheControl is null
            ? null
            : new AnthropicCacheControl { Type = cacheControl.Type, Ttl = cacheControl.Ttl };

    private static JsonElement NormalizeInputSchema(JsonElement? parameters)
    {
        if (parameters is not { } schema || schema.ValueKind == JsonValueKind.Null)
        {
            return EmptyInputSchema;
        }

        return schema;
    }

    private static string GetMetaString(Dictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return value.GetString() ?? string.Empty;
    }

    private static AnthropicCacheControl? GetMetaCacheControl(Dictionary<string, JsonElement> metadata)
    {
        if (!metadata.TryGetValue(AnthropicJson.KeyCacheControl, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return value.Deserialize<AnthropicCacheControl>(LlmJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static List<string>? EmptyToNull(List<string>? value)
        => value is { Count: > 0 } ? value : null;

    /// <summary>Port of lo.Uniq: keeps the first occurrence of every value.</summary>
    private static IEnumerable<string> Distinct(List<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (seen.Add(value))
            {
                yield return value;
            }
        }
    }
}
