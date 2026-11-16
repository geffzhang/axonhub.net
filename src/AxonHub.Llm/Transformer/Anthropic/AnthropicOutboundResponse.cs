using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of the response half of llm/transformer/anthropic/outbound_convert.go:
/// an Anthropic Messages response becomes the unified response, preserving the
/// thinking items, citations and provider-native tool blocks the round-trip
/// needs.
/// </summary>
public static class AnthropicOutboundResponse
{
    /// <summary>Port of convertToLlmResponse.</summary>
    public static LlmResponse ConvertToLlmResponse(
        AnthropicMessage? anthropicResp,
        AnthropicPlatformType platformType)
    {
        if (anthropicResp is null)
        {
            return new LlmResponse
            {
                Id = string.Empty,
                Object = "chat.completion",
                Model = string.Empty,
                Created = 0,
            };
        }

        var response = new LlmResponse
        {
            Id = anthropicResp.Id,
            Object = "chat.completion",
            Model = anthropicResp.Model,
            Created = 0, // Anthropic doesn't provide a created timestamp.
            RequestType = RequestTypes.Chat,
            ApiFormat = ApiFormats.AnthropicMessage,
        };

        var contentParts = new List<MessageContentPart>();
        var reasoningItems = new List<ReasoningItem>();
        string? singleThinkingText = null;
        string? singleThinkingSignature = null;
        string? redactedThinkingData = null;
        var toolCalls = new List<ToolCall>();
        var annotations = new List<Annotation>();
        var textParts = new List<string>();
        var inlineToolResults = new List<InlineToolResult>();
        Dictionary<string, JsonElement>? transformerMetadata = null;

        for (var i = 0; i < anthropicResp.Content?.Count; i++)
        {
            var block = anthropicResp.Content[i];

            if (block.Type == "text")
            {
                if (!string.IsNullOrEmpty(block.Text))
                {
                    textParts.Add(block.Text);
                    var part = new MessageContentPart
                    {
                        Type = "text",
                        Text = block.Text,
                        // The reference always attaches an empty image_url
                        // object to text parts.
                        ImageUrl = new ImageUrl(),
                    };
                    part.TransformerMetadata = AnthropicJson.SetBlockIndex(
                        part.TransformerMetadata,
                        i);
                    contentParts.Add(part);
                }

                if (block.Citations is { Count: > 0 } citations)
                {
                    foreach (var citation in citations)
                    {
                        // Go decodes a null element as the zero TextCitation
                        // (empty type), which yields no annotation.
                        if (LlmAnnotationFromCitation(citation ?? new AnthropicTextCitation()) is { } annotation)
                        {
                            annotations.Add(annotation);
                        }
                    }
                }
            }
            else if (block.Type == "image")
            {
                if (block.Source is not null)
                {
                    // The reference reads Source.Data, not Source.URL.
                    contentParts.Add(new MessageContentPart
                    {
                        Type = "image",
                        ImageUrl = new ImageUrl { Url = block.Source.Data ?? string.Empty },
                    });
                }
            }
            else if (block.Type == "tool_use")
            {
                if (!string.IsNullOrEmpty(block.Id) && block.Name is not null)
                {
                    var toolCall = ToolCallFromAnthropicBlock(block);
                    toolCall.TransformerMetadata = AnthropicJson.SetBlockIndex(
                        toolCall.TransformerMetadata,
                        i);
                    toolCalls.Add(toolCall);
                }
            }
            else if (block.Type == "thinking")
            {
                singleThinkingText = block.Thinking;
                singleThinkingSignature = block.Signature;

                var item = new ReasoningItem
                {
                    Content = block.Thinking ?? string.Empty,
                    Signature = AnthropicSignatures.EncodeAnthropicSignature(block.Signature)
                        ?? string.Empty,
                };
                if (item.Content.Length > 0 || item.Signature.Length > 0)
                {
                    reasoningItems.Add(item);
                }
            }
            else if (block.Type == "redacted_thinking")
            {
                if (!string.IsNullOrEmpty(block.Data))
                {
                    redactedThinkingData = block.Data;
                }
            }
            else
            {
                if (AnthropicJson.IsSpecialToolUseBlock(block.Type))
                {
                    if (!string.IsNullOrEmpty(block.Id) && block.Name is not null)
                    {
                        var toolCall = ToolCallFromAnthropicBlock(block);
                        toolCall.TransformerMetadata = AnthropicJson.SetBlockIndex(
                            toolCall.TransformerMetadata,
                            i);
                        toolCalls.Add(toolCall);
                    }

                    transformerMetadata ??= [];
                    transformerMetadata[AnthropicJson.KeyResponseContent] =
                        JsonSerializer.SerializeToElement(
                            CloneAnthropicResponseContentBlocks(anthropicResp.Content) ?? [],
                            LlmJson.Options);
                }
                else if (AnthropicJson.IsSpecialToolResultBlock(block.Type))
                {
                    var inlineResult = AnthropicConvertToLlm.InlineToolResultFromBlock(block);
                    inlineResult.TransformerMetadata = AnthropicJson.SetBlockIndex(
                        inlineResult.TransformerMetadata,
                        i);
                    inlineToolResults.Add(inlineResult);

                    transformerMetadata ??= [];
                    transformerMetadata[AnthropicJson.KeyResponseContent] =
                        JsonSerializer.SerializeToElement(
                            CloneAnthropicResponseContentBlocks(anthropicResp.Content) ?? [],
                            LlmJson.Options);
                }
            }
        }

        // Go leaves MultipleContent nil when nothing was appended; the
        // converter then writes a JSON null for the whole content value.
        var content = new MessageContent
        {
            MultipleContent = contentParts.Count > 0 ? contentParts : null,
        };

        // Collapse text-only MultipleContent into Content.Content whenever doing
        // so does not lose ordering information needed for the Anthropic
        // round-trip: it is safe iff every tool call and every inline tool
        // result has a block index strictly greater than every text part's
        // block index. When a server-side tool appears *between* text blocks,
        // keep MultipleContent so per-part anthropic_block_index metadata
        // survives.
        if (textParts.Count > 0 && contentParts.Count == textParts.Count)
        {
            var maxTextIndex = -1;
            foreach (var part in contentParts)
            {
                var index = AnthropicJson.GetBlockIndex(part.TransformerMetadata);
                if (index > maxTextIndex)
                {
                    maxTextIndex = index;
                }
            }

            var safeToCollapse = true;

            foreach (var toolCall in toolCalls)
            {
                var index = AnthropicJson.GetBlockIndex(toolCall.TransformerMetadata);
                if (index >= 0 && index < maxTextIndex)
                {
                    safeToCollapse = false;
                    break;
                }
            }

            if (safeToCollapse)
            {
                foreach (var inlineResult in inlineToolResults)
                {
                    var index = AnthropicJson.GetBlockIndex(inlineResult.TransformerMetadata);
                    if (index >= 0 && index < maxTextIndex)
                    {
                        safeToCollapse = false;
                        break;
                    }
                }
            }

            if (safeToCollapse)
            {
                content.Content = string.Concat(textParts);
                content.MultipleContent = null;
            }
        }

        var message = new Message
        {
            Role = anthropicResp.Role,
            Content = content,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            RedactedReasoningContent = redactedThinkingData,
            Annotations = annotations.Count > 0 ? annotations : null,
            InlineToolResults = inlineToolResults.Count > 0 ? inlineToolResults : null,
        };

        if (reasoningItems.Count == 1)
        {
            // Preserve the legacy single-item representation exactly;
            // downstream converters still accept it as the compatibility
            // fallback.
            message.ReasoningContent = singleThinkingText;
            message.ReasoningSignature =
                AnthropicSignatures.EncodeAnthropicSignature(singleThinkingSignature);
        }
        else if (reasoningItems.Count > 1)
        {
            message.ReasoningItems = reasoningItems;

            // OpenAI Chat and other non-Responses clients consume scalar
            // reasoning fields only. Preserve a readable aggregate while
            // retaining each item's individual signature in ReasoningItems for
            // Responses round-trips.
            var aggregateReasoning = string.Concat(reasoningItems.Select(static item => item.Content));
            if (aggregateReasoning.Length > 0)
            {
                message.ReasoningContent = aggregateReasoning;
            }

            var signature = reasoningItems[^1].Signature;
            if (!string.IsNullOrEmpty(signature))
            {
                message.ReasoningSignature = signature;
            }
        }

        response.Choices =
        [
            new Choice
            {
                Index = 0,
                Message = message,
                FinishReason = ConvertToLlmFinishReason(anthropicResp.StopReason),
            },
        ];

        response.Usage = AnthropicAggregator.ConvertToLlmUsage(anthropicResp.Usage, platformType);
        if (transformerMetadata is not null)
        {
            response.TransformerMetadata = transformerMetadata;
        }

        return response;
    }

    /// <summary>Port of convertToLlmFinishReason.</summary>
    public static string? ConvertToLlmFinishReason(string? stopReason)
    {
        if (stopReason is null)
        {
            return null;
        }

        return stopReason switch
        {
            "end_turn" => "stop",
            "max_tokens" => "length",
            "stop_sequence" or "pause_turn" => "stop",
            "tool_use" => "tool_calls",
            "refusal" => "content_filter",
            _ => stopReason,
        };
    }

    /// <summary>
    /// Port of toolCallFromAnthropicBlock. For special blocks,
    /// TransformerMetadata is populated with anthropic_type (+ optional
    /// anthropic_caller) so the block can be round-tripped.
    /// </summary>
    public static ToolCall ToolCallFromAnthropicBlock(AnthropicMessageContentBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var repaired = AnthropicJson.SafeJsonRawMessage(block.Input?.GetRawText());

        var toolCall = new ToolCall
        {
            Id = block.Id,
            Type = "function",
            Function = new FunctionCall
            {
                Name = block.Name ?? string.Empty,
                Arguments = System.Text.Encoding.UTF8.GetString(repaired),
            },
            CacheControl = AnthropicConvertToLlm.ConvertCacheControl(block.CacheControl),
        };

        toolCall.TransformerMetadata = AnthropicJson.SetSpecialMeta(
            toolCall.TransformerMetadata,
            block.Type,
            block.Caller);

        return toolCall;
    }

    /// <summary>Port of llmAnnotationFromCitation.</summary>
    public static Annotation? LlmAnnotationFromCitation(AnthropicTextCitation citation)
    {
        ArgumentNullException.ThrowIfNull(citation);

        if (string.IsNullOrEmpty(citation.Type))
        {
            return null;
        }

        var annotation = new Annotation { Type = citation.Type };
        var url = citation.Url ?? string.Empty;
        var title = citation.Title ?? string.Empty;
        if (url.Length > 0 || title.Length > 0)
        {
            annotation.UrlCitation = new UrlCitation { Url = url, Title = title };
        }

        return annotation;
    }

    /// <summary>Port of cloneAnthropicResponseContentBlocks (xjson.MustTo).</summary>
    public static List<AnthropicMessageContentBlock>? CloneAnthropicResponseContentBlocks(
        List<AnthropicMessageContentBlock>? blocks)
    {
        if (blocks is null || blocks.Count == 0)
        {
            return null;
        }

        var data = GoJson.Marshal(blocks);
        return LlmJson.Deserialize<List<AnthropicMessageContentBlock>>(data);
    }
}
