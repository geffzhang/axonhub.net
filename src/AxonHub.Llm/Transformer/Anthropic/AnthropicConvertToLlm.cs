using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of inbound_convert.go's request direction (convertToLLMRequest and the
/// helpers it calls).
/// </summary>
public static class AnthropicConvertToLlm
{
    /// <summary>Port of convertToLLMRequest.</summary>
    public static LlmRequest ConvertToLlmRequest(AnthropicMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatRequest = new LlmRequest
        {
            Model = request.Model ?? string.Empty,
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            TopP = request.TopP,
            Stream = request.Stream,
            Metadata = [],
            RequestType = RequestTypes.Chat,
            ApiFormat = ApiFormats.AnthropicMessage,
            TransformerMetadata = [],
            TransformOptions = new TransformOptions(),
        };

        if (request.Metadata is not null)
        {
            chatRequest.Metadata!["user_id"] = request.Metadata.UserId ?? string.Empty;
        }

        // The top-level cache_control travels untouched so the Anthropic
        // outbound transformer can restore it and skip its own optimization.
        if (request.CacheControl is not null)
        {
            chatRequest.TransformerMetadata![AnthropicJson.KeyCacheControl] =
                JsonSerializer.SerializeToElement(request.CacheControl, LlmJson.Options);
        }

        var messages = new List<Message>(request.Messages?.Count ?? 0);

        if (request.System is not null)
        {
            if (request.System.Prompt is not null)
            {
                messages.Add(new Message
                {
                    Role = "system",
                    Content = MessageContent.FromText(request.System.Prompt),
                });
            }
            else if (request.System.MultiplePrompts is { Count: > 0 })
            {
                chatRequest.TransformOptions!.ArrayInstructions = true;

                foreach (var prompt in request.System.MultiplePrompts)
                {
                    messages.Add(new Message
                    {
                        Role = "system",
                        Content = MessageContent.FromText(prompt.Text ?? string.Empty),
                        CacheControl = ConvertCacheControl(prompt.CacheControl),
                    });
                }
            }
        }

        if (request.Messages is not null)
        {
            for (var messageIndex = 0; messageIndex < request.Messages.Count; messageIndex++)
            {
                ConvertMessage(request.Messages[messageIndex], messageIndex, messages);
            }
        }

        chatRequest.Messages = messages;

        if (request.Tools is { Count: > 0 })
        {
            var tools = new List<Tool>(request.Tools.Count);
            foreach (var tool in request.Tools)
            {
                if (ConvertToolToLlm(tool, out var llmTool))
                {
                    tools.Add(llmTool);
                }
            }

            chatRequest.Tools = tools;
        }

        if (request.StopSequences is { Count: > 0 })
        {
            // Go decodes a null element as ""; STJ keeps null, so normalize.
            var stopSequences = request.StopSequences
                .Select(static stop => stop ?? string.Empty)
                .ToList();
            chatRequest.Stop = stopSequences.Count == 1
                ? new Stop { Single = stopSequences[0] }
                : new Stop { Multiple = stopSequences };
        }

        if (request.ToolChoice is not null)
        {
            chatRequest.ToolChoice = ConvertToolChoiceToLlm(request.ToolChoice);

            if (request.ToolChoice.DisableParallelToolUse is { } disable)
            {
                chatRequest.ParallelToolCalls = !disable;
            }
        }

        if (request.Thinking is not null)
        {
            ConvertThinking(request.Thinking, chatRequest);
        }

        if (request.OutputConfig is not null && !string.IsNullOrEmpty(request.OutputConfig.Effort))
        {
            chatRequest.TransformerMetadata![AnthropicJson.KeyOutputConfigEffort] =
                JsonSerializer.SerializeToElement(request.OutputConfig.Effort);
            // The client sent an explicit effort: pass it through verbatim
            // (including "max") while keeping the native value in metadata.
            chatRequest.ReasoningEffort = request.OutputConfig.Effort;
        }

        return chatRequest;
    }

    /// <summary>Port of convertToLLMCacheControl.</summary>
    public static CacheControl? ConvertCacheControl(AnthropicCacheControl? cacheControl)
        => cacheControl is null
            ? null
            : new CacheControl { Type = cacheControl.Type, Ttl = cacheControl.Ttl };

    private static void ConvertMessage(
        AnthropicMessageParam? message,
        int messageIndex,
        List<Message> messages)
    {
        var chatMessage = new Message { Role = message?.Role ?? string.Empty };

        var hasContent = false;
        var hasToolResult = false;
        var content = message?.Content;

        if (content?.Content is not null)
        {
            chatMessage.Content = MessageContent.FromText(content.Content);
            hasContent = true;
        }
        else if (content?.MultipleContent is { Count: > 0 })
        {
            var contentParts = new List<MessageContentPart>(content.MultipleContent.Count);
            string? redactedReasoningContent = null;
            var reasoningItems = new List<ReasoningItem>();

            for (var blockIndex = 0; blockIndex < content.MultipleContent.Count; blockIndex++)
            {
                var block = content.MultipleContent[blockIndex] ?? new AnthropicMessageContentBlock();

                switch (block.Type ?? string.Empty)
                {
                    case "thinking":
                    {
                        var item = new ReasoningItem
                        {
                            Content = EmptyToNull(block.Thinking),
                            Signature = EmptyToNull(block.Signature),
                        };
                        if (item.Content is not null || item.Signature is not null)
                        {
                            reasoningItems.Add(item);
                        }

                        break;
                    }

                    case "redacted_thinking":
                        if (!string.IsNullOrEmpty(block.Data))
                        {
                            redactedReasoningContent = block.Data;
                        }

                        break;

                    case "text":
                    {
                        var part = new MessageContentPart
                        {
                            Type = "text",
                            Text = block.Text,
                            CacheControl = ConvertCacheControl(block.CacheControl),
                        };
                        part.TransformerMetadata =
                            AnthropicJson.SetBlockIndex(part.TransformerMetadata, blockIndex);
                        contentParts.Add(part);
                        hasContent = true;
                        break;
                    }

                    case "image":
                        if (ConvertImageSourceToLlmImageUrlPart(block.Source, block.CacheControl, out var imagePart))
                        {
                            contentParts.Add(imagePart);
                            hasContent = true;
                        }

                        break;

                    case "tool_result":
                        hasToolResult = true;
                        if (block.Content is not null)
                        {
                            ConvertToolResult(block, messageIndex, messages);
                        }

                        break;

                    case "tool_use":
                    {
                        var toolCall = new ToolCall
                        {
                            Id = block.Id,
                            Type = "function",
                            Function = new FunctionCall
                            {
                                Name = block.Name ?? string.Empty,
                                Arguments = RawText(block.Input),
                            },
                            CacheControl = ConvertCacheControl(block.CacheControl),
                        };
                        toolCall.TransformerMetadata =
                            AnthropicJson.SetBlockIndex(toolCall.TransformerMetadata, blockIndex);
                        (chatMessage.ToolCalls ??= []).Add(toolCall);
                        hasContent = true;
                        break;
                    }

                    default:
                        hasContent |= ConvertSpecialBlock(
                            block,
                            blockIndex,
                            chatMessage,
                            ref hasToolResult);
                        break;
                }
            }

            if (contentParts.Count == 1 && contentParts[0].Type == "text")
            {
                // A single text block collapses to the bare-string form; the
                // block's cache control moves up to the message level.
                chatMessage.Content = MessageContent.FromText(contentParts[0].Text ?? string.Empty);
                chatMessage.CacheControl = contentParts[0].CacheControl;
                hasContent = true;
            }
            else if (contentParts.Count > 0)
            {
                chatMessage.Content = MessageContent.FromParts(contentParts);
                hasContent = true;
            }

            if (reasoningItems.Count > 0)
            {
                chatMessage.ReasoningItems = reasoningItems;

                // Scalar fallback for outbound transformers that do not consume
                // ReasoningItems: concatenated content, last non-empty signature.
                var aggregate = string.Concat(reasoningItems.Select(static item => item.Content ?? string.Empty));
                if (aggregate.Length > 0)
                {
                    chatMessage.ReasoningContent = aggregate;
                }

                var signature = reasoningItems[^1].Signature;
                if (!string.IsNullOrEmpty(signature))
                {
                    chatMessage.ReasoningSignature = signature;
                }

                hasContent = true;
            }

            if (!string.IsNullOrEmpty(redactedReasoningContent))
            {
                chatMessage.RedactedReasoningContent = redactedReasoningContent;
                hasContent = true;
            }
        }

        if (!hasContent)
        {
            return;
        }

        if (hasToolResult)
        {
            chatMessage.MessageIndex = messageIndex;
        }

        messages.Add(chatMessage);
    }

    private static void ConvertToolResult(
        AnthropicMessageContentBlock block,
        int messageIndex,
        List<Message> messages)
    {
        var toolMessage = new Message
        {
            Role = "tool",
            MessageIndex = messageIndex,
            ToolCallId = block.ToolUseId,
            CacheControl = ConvertCacheControl(block.CacheControl),
            ToolCallIsError = block.IsError,
        };

        var toolContent = block.Content!;
        if (toolContent.Content is not null)
        {
            toolMessage.Content = MessageContent.FromText(toolContent.Content);
        }
        else if (toolContent.MultipleContent is { Count: > 0 })
        {
            var parts = new List<MessageContentPart>(toolContent.MultipleContent.Count);
            foreach (var contentBlock in toolContent.MultipleContent)
            {
                switch (contentBlock?.Type)
                {
                    case "text":
                        parts.Add(new MessageContentPart
                        {
                            Type = "text",
                            Text = contentBlock.Text,
                            CacheControl = ConvertCacheControl(contentBlock.CacheControl),
                        });
                        break;
                    case "image":
                        if (ConvertImageSourceToLlmImageUrlPart(
                                contentBlock.Source,
                                contentBlock.CacheControl,
                                out var imagePart))
                        {
                            parts.Add(imagePart);
                        }

                        break;
                }
            }

            toolMessage.Content = parts.Count > 0
                ? MessageContent.FromParts(parts)
                // Keep tool content non-null for downstream conversions.
                : MessageContent.FromText(string.Empty);
        }

        messages.Add(toolMessage);
    }

    private static bool ConvertSpecialBlock(
        AnthropicMessageContentBlock block,
        int blockIndex,
        Message chatMessage,
        ref bool hasToolResult)
    {
        var blockType = block.Type ?? string.Empty;

        if (AnthropicJson.IsSpecialToolUseBlock(blockType))
        {
            var toolCall = new ToolCall
            {
                Id = block.Id,
                Type = "function",
                Function = new FunctionCall
                {
                    Name = block.Name ?? string.Empty,
                    Arguments = RawText(block.Input),
                },
                CacheControl = ConvertCacheControl(block.CacheControl),
            };
            toolCall.TransformerMetadata =
                AnthropicJson.SetSpecialMeta(toolCall.TransformerMetadata, blockType, block.Caller);
            toolCall.TransformerMetadata =
                AnthropicJson.SetBlockIndex(toolCall.TransformerMetadata, blockIndex);
            (chatMessage.ToolCalls ??= []).Add(toolCall);
            return true;
        }

        if (AnthropicJson.IsSpecialToolResultBlock(blockType))
        {
            var inlineResult = InlineToolResultFromBlock(block);
            inlineResult.TransformerMetadata =
                AnthropicJson.SetBlockIndex(inlineResult.TransformerMetadata, blockIndex);
            (chatMessage.InlineToolResults ??= []).Add(inlineResult);
            hasToolResult = true;
            return true;
        }

        return false;
    }

    /// <summary>Port of inlineToolResultFromBlock.</summary>
    public static InlineToolResult InlineToolResultFromBlock(AnthropicMessageContentBlock block)
    {
        var result = new InlineToolResult();

        if (block.ToolUseId is not null)
        {
            result.ToolCallId = block.ToolUseId;
        }

        var rawContent = MarshalToolResultContent(block.Content);
        if (rawContent is { Length: > 0 })
        {
            result.Output = System.Text.Encoding.UTF8.GetString(rawContent);
        }

        if (block.IsError == true || HasErrorTypeSuffix(rawContent))
        {
            result.IsError = true;
        }

        result.TransformerMetadata =
            AnthropicJson.SetSpecialMeta(result.TransformerMetadata, block.Type, block.Caller);
        result.TransformerMetadata =
            AnthropicJson.SetToolResultContent(result.TransformerMetadata, rawContent);

        return result;
    }

    /// <summary>Port of convertImageSourceToLLMImageURLPart.</summary>
    public static bool ConvertImageSourceToLlmImageUrlPart(
        AnthropicImageSource? source,
        AnthropicCacheControl? cacheControl,
        out MessageContentPart part)
    {
        part = new MessageContentPart
        {
            Type = "image_url",
            CacheControl = ConvertCacheControl(cacheControl),
        };

        if (source is null)
        {
            return false;
        }

        if (source.Type == "base64")
        {
            if (string.IsNullOrEmpty(source.Data))
            {
                return false;
            }

            var mediaType = string.IsNullOrEmpty(source.MediaType)
                ? "application/octet-stream"
                : source.MediaType;
            part.ImageUrl = new ImageUrl
            {
                Url = AnthropicJson.BuildDataUrl(mediaType, source.Data, true),
            };
            return true;
        }

        if (string.IsNullOrEmpty(source.Url))
        {
            return false;
        }

        part.ImageUrl = new ImageUrl { Url = source.Url };
        return true;
    }

    /// <summary>Port of convertToolToLLM.</summary>
    public static bool ConvertToolToLlm(AnthropicTool? tool, out Tool llmTool)
    {
        var type = tool?.Type ?? string.Empty;

        switch (type)
        {
            case "web_search_20250305":
            case "web_search":
            {
                var location = tool!.UserLocation ?? new AnthropicWebSearchToolUserLocation();
                llmTool = new Tool
                {
                    Type = ToolTypes.WebSearch,
                    CacheControl = ConvertCacheControl(tool.CacheControl),
                    WebSearch = new WebSearch
                    {
                        MaxUses = tool.MaxUses,
                        Strict = tool.Strict,
                        // Go decodes a null element as ""; STJ keeps null.
                        AllowedDomains = tool.AllowedDomains?
                            .Select(static domain => domain ?? string.Empty)
                            .ToList(),
                        BlockedDomains = tool.BlockedDomains?
                            .Select(static domain => domain ?? string.Empty)
                            .ToList(),
                        UserLocation = new WebSearchToolUserLocation
                        {
                            City = location.City,
                            Country = location.Country,
                            Region = location.Region,
                            Timezone = location.Timezone,
                            Type = location.Type ?? string.Empty,
                        },
                    },
                };
                return true;
            }

            case "":
            case "custom":
                llmTool = new Tool
                {
                    Type = ToolTypes.Function,
                    Function = new Function
                    {
                        Name = tool?.Name ?? string.Empty,
                        Description = tool?.Description,
                        Parameters = tool?.InputSchema,
                        Strict = tool?.Strict,
                    },
                    CacheControl = ConvertCacheControl(tool?.CacheControl),
                };
                return true;

            default:
                llmTool = new Tool();
                return false;
        }
    }

    /// <summary>Port of convertAnthropicToolChoiceToLLM.</summary>
    public static ToolChoice? ConvertToolChoiceToLlm(AnthropicToolChoice? source)
    {
        if (source is null)
        {
            return null;
        }

        switch (source.Type)
        {
            case "auto":
            case "none":
                return new ToolChoice { ToolChoiceMode = source.Type };
            case "any":
                return new ToolChoice { ToolChoiceMode = "required" };
            case "tool" when source.Name is not null:
                return new ToolChoice
                {
                    NamedToolChoice = new NamedToolChoice
                    {
                        Type = "function",
                        Function = new ToolFunction { Name = source.Name },
                    },
                };
            default:
                return null;
        }
    }

    /// <summary>Port of thinkingBudgetToReasoningEffort.</summary>
    public static string ThinkingBudgetToReasoningEffort(long budgetTokens) => budgetTokens switch
    {
        <= 5000 => "low",
        <= 15000 => "medium",
        <= 30000 => "high",
        _ => "xhigh",
    };

    private static void ConvertThinking(AnthropicThinking thinking, LlmRequest chatRequest)
    {
        switch (thinking.Type)
        {
            case "enabled":
                chatRequest.TransformerMetadata![AnthropicJson.KeyThinkingType] =
                    JsonSerializer.SerializeToElement("enabled");
                if (thinking.BudgetTokens is > 0 and var budget)
                {
                    chatRequest.ReasoningEffort = ThinkingBudgetToReasoningEffort(budget);
                    chatRequest.ReasoningBudget = budget;
                }
                else
                {
                    // No budget given: fall back to medium so the outbound side
                    // uses its channel mapping instead of budget_tokens=0.
                    chatRequest.ReasoningEffort = "medium";
                }

                if (!string.IsNullOrEmpty(thinking.Display))
                {
                    chatRequest.TransformerMetadata[AnthropicJson.KeyThinkingDisplay] =
                        JsonSerializer.SerializeToElement(thinking.Display);
                }

                break;

            case "disabled":
                chatRequest.TransformerMetadata![AnthropicJson.KeyThinkingType] =
                    JsonSerializer.SerializeToElement("disabled");
                chatRequest.ReasoningEffort = "none";
                break;

            case "adaptive":
                chatRequest.TransformerMetadata![AnthropicJson.KeyThinkingType] =
                    JsonSerializer.SerializeToElement("adaptive");
                chatRequest.ReasoningEffort = "high";

                if (!string.IsNullOrEmpty(thinking.Display))
                {
                    chatRequest.TransformerMetadata[AnthropicJson.KeyThinkingDisplay] =
                        JsonSerializer.SerializeToElement(thinking.Display);
                }

                break;
        }
    }

    /// <summary>
    /// Port of marshalToolResultContent: Go <c>json.Marshal(*MessageContent)</c>,
    /// including the "null" result for an empty value.
    /// </summary>
    internal static byte[]? MarshalToolResultContent(AnthropicMessageContent? content)
    {
        if (content is null)
        {
            return null;
        }

        if (content.Raw is { Length: > 0 })
        {
            return GoJson.MarshalRaw(content.Raw);
        }

        if (content.Content is not null)
        {
            return GoJson.Marshal(content.Content);
        }

        if (content.MultipleContent is not null)
        {
            return GoJson.Marshal(content.MultipleContent);
        }

        return "null"u8.ToArray();
    }

    /// <summary>Port of hasErrorTypeSuffix.</summary>
    internal static bool HasErrorTypeSuffix(byte[]? raw)
    {
        if (raw is not { Length: > 0 })
        {
            return false;
        }

        var element = AnthropicJson.TryRawElement(raw);
        if (element is null)
        {
            return false;
        }

        var root = element.Value;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Object
                    && item.TryGetProperty("type", out var type)
                    && type.ValueKind == System.Text.Json.JsonValueKind.String
                    && (type.GetString()?.EndsWith("_error", StringComparison.Ordinal) ?? false))
                {
                    return true;
                }
            }

            return false;
        }

        return root.ValueKind == System.Text.Json.JsonValueKind.Object
               && root.TryGetProperty("type", out var rootType)
               && rootType.ValueKind == System.Text.Json.JsonValueKind.String
               && (rootType.GetString()?.EndsWith("_error", StringComparison.Ordinal) ?? false);
    }

    /// <summary>Go <c>string(json.RawMessage)</c>: empty for an absent input.</summary>
    private static string RawText(JsonElement? input)
        => input is { } value ? value.GetRawText() : string.Empty;

    /// <summary>Go's omitempty on string fields: "" and null both disappear.</summary>
    private static string? EmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
