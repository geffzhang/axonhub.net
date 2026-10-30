using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of inbound_convert.go's response direction (convertToAnthropicResponse,
/// the citation helpers and the anthropic_response_content merge).
/// </summary>
public static class AnthropicConvertFromLlm
{
    private const string OpenAIResponsesWebSearchCallsMetadata = "openai_responses_web_search_calls";

    /// <summary>Port of convertToAnthropicResponse.</summary>
    public static AnthropicMessage ResponseFromLlm(LlmResponse chatResponse)
    {
        ArgumentNullException.ThrowIfNull(chatResponse);

        var response = new AnthropicMessage
        {
            Id = chatResponse.Id,
            Type = "message",
            Role = "assistant",
            Model = chatResponse.Model,
        };

        if (chatResponse.Choices.Count > 0)
        {
            var choice = chatResponse.Choices[0];
            var message = choice.Message ?? choice.Delta;

            if (message is not null)
            {
                var contentBlocks = new List<AnthropicMessageContentBlock>();

                if (!string.IsNullOrEmpty(message.ReasoningContent)
                    || !string.IsNullOrEmpty(message.ReasoningSignature))
                {
                    contentBlocks.Add(new AnthropicMessageContentBlock
                    {
                        Type = "thinking",
                        Thinking = message.ReasoningContent ?? string.Empty,
                        Signature = message.ReasoningSignature ?? AnthropicJson.GenerateSignature(),
                    });
                }

                if (!string.IsNullOrEmpty(message.RedactedReasoningContent))
                {
                    contentBlocks.Add(new AnthropicMessageContentBlock
                    {
                        Type = "redacted_thinking",
                        Data = message.RedactedReasoningContent,
                    });
                }

                // Collect text / image / tool_use / *_tool_result into one list
                // so blocks tagged with anthropic_block_index interleave
                // faithfully with the leading text block.
                var ordered = new List<OrderedContentBlock>();
                AnthropicMessageContentBlock? leadingBlock = null;

                void AppendOrdered(
                    Dictionary<string, JsonElement>? metadata,
                    AnthropicMessageContentBlock block)
                {
                    ordered.Add(new OrderedContentBlock(
                        AnthropicJson.GetBlockIndex(metadata),
                        ordered.Count,
                        block));
                }

                if (message.Content?.Content is { Length: > 0 } leadingText)
                {
                    // A collapsed single-string text always precedes tool calls
                    // and results: outbound_convert only collapses when true.
                    leadingBlock = new AnthropicMessageContentBlock
                    {
                        Type = "text",
                        Text = leadingText,
                    };
                }
                else if (message.Content?.MultipleContent is { Count: > 0 } multipleContent)
                {
                    foreach (var part in multipleContent)
                    {
                        if (part is null)
                        {
                            continue;
                        }

                        switch (part.Type)
                        {
                            case "text":
                                if (part.Text is not null)
                                {
                                    AppendOrdered(part.TransformerMetadata, new AnthropicMessageContentBlock
                                    {
                                        Type = "text",
                                        Text = part.Text,
                                    });
                                }

                                break;

                            case "image_url":
                                if (part.ImageUrl is { Url.Length: > 0 } imageUrl)
                                {
                                    var parsed = AnthropicJson.ParseDataUrl(imageUrl.Url);
                                    AppendOrdered(
                                        part.TransformerMetadata,
                                        parsed is not null
                                            ? new AnthropicMessageContentBlock
                                            {
                                                Type = "image",
                                                Source = new AnthropicImageSource
                                                {
                                                    Type = "base64",
                                                    MediaType = parsed.MediaType,
                                                    Data = parsed.Data,
                                                },
                                            }
                                            : new AnthropicMessageContentBlock
                                            {
                                                Type = "image",
                                                Source = new AnthropicImageSource
                                                {
                                                    Type = "url",
                                                    Url = imageUrl.Url,
                                                },
                                            });
                                }

                                break;
                        }
                    }
                }

                if (message.ToolCalls is { Count: > 0 } toolCalls)
                {
                    foreach (var toolCall in toolCalls)
                    {
                        var input = string.IsNullOrEmpty(toolCall.Function.Arguments)
                            ? AnthropicJson.RawElement("{}"u8)
                            : AnthropicJson.RawElement(AnthropicJson.SafeJsonRawMessage(toolCall.Function.Arguments));
                        input = AnthropicJson.SanitizeReadToolInput(toolCall.Function.Name, input);

                        var blockType = AnthropicJson.GetAnthropicType(toolCall.TransformerMetadata);
                        if (blockType.Length == 0)
                        {
                            blockType = "tool_use";
                        }

                        AppendOrdered(toolCall.TransformerMetadata, new AnthropicMessageContentBlock
                        {
                            Type = blockType,
                            Id = toolCall.Id ?? string.Empty,
                            Name = toolCall.Function.Name,
                            Input = input,
                            Caller = AnthropicJson.GetAnthropicCaller(toolCall.TransformerMetadata),
                        });
                    }
                }

                if (message.InlineToolResults is { Count: > 0 } inlineResults)
                {
                    foreach (var inlineResult in inlineResults)
                    {
                        if (ToolResultBlockFromInline(inlineResult, out var block))
                        {
                            AppendOrdered(inlineResult.TransformerMetadata, block);
                        }
                    }
                }

                if (leadingBlock is not null)
                {
                    contentBlocks.Add(leadingBlock);
                }

                contentBlocks.AddRange(SortOrderedContentBlocks(ordered));

                response.Content = MergeAnthropicResponseContentBlocks(
                    contentBlocks,
                    chatResponse.TransformerMetadata,
                    message.Annotations);
            }

            if (choice.FinishReason is { } finishReason)
            {
                response.StopReason = finishReason switch
                {
                    "stop" => "end_turn",
                    "length" => "max_tokens",
                    "tool_calls" => "tool_use",
                    _ => finishReason,
                };
            }
            else
            {
                response.StopReason = response.Content?.Any(static block => block.Type == "tool_use") == true
                    ? "tool_use"
                    : "end_turn";
            }
        }

        if (chatResponse.Usage is { } usage)
        {
            response.Usage = ConvertToAnthropicUsage(usage);
        }

        return response;
    }

    /// <summary>Port of convertToAnthropicUsage.</summary>
    public static AnthropicUsage ConvertToAnthropicUsage(Usage llmUsage)
    {
        ArgumentNullException.ThrowIfNull(llmUsage);

        var usage = new AnthropicUsage
        {
            InputTokens = llmUsage.PromptTokens,
            OutputTokens = llmUsage.CompletionTokens,
            Cost = llmUsage.Cost,
        };

        if (llmUsage.PromptTokensDetails is { } details)
        {
            usage.CacheReadInputTokens = details.CachedTokens;
            usage.CacheCreationInputTokens = details.WriteCachedTokens;
            usage.CacheCreation = new AnthropicCacheCreation
            {
                Ephemeral5mInputTokens = details.WriteCached5MinTokens,
                Ephemeral1hInputTokens = details.WriteCached1HourTokens,
            };
            usage.InputTokens -= usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
        }

        return usage;
    }

    /// <summary>Port of toolResultBlockFromInline.</summary>
    public static bool ToolResultBlockFromInline(
        InlineToolResult inlineResult,
        out AnthropicMessageContentBlock block)
    {
        var blockType = AnthropicJson.GetAnthropicType(inlineResult.TransformerMetadata);
        if (blockType.Length == 0)
        {
            block = new AnthropicMessageContentBlock();
            return false;
        }

        block = new AnthropicMessageContentBlock
        {
            Type = blockType,
            Caller = AnthropicJson.GetAnthropicCaller(inlineResult.TransformerMetadata),
        };

        if (!string.IsNullOrEmpty(inlineResult.ToolCallId))
        {
            block.ToolUseId = inlineResult.ToolCallId;
        }

        if (inlineResult.IsError)
        {
            block.IsError = true;
        }

        if (AnthropicJson.GetAnthropicToolResultContent(inlineResult.TransformerMetadata) is { } rawContent)
        {
            var content = new AnthropicMessageContent();
            content.SetRaw(Encoding.UTF8.GetBytes(rawContent.GetRawText()));
            block.Content = content;
        }
        else if (!string.IsNullOrEmpty(inlineResult.Output))
        {
            block.Content = new AnthropicMessageContent { Content = inlineResult.Output };
        }

        return true;
    }

    /// <summary>Port of citationFromLLMAnnotation.</summary>
    public static AnthropicTextCitation? CitationFromLlmAnnotation(
        Annotation annotation,
        Dictionary<string, JsonElement>? metadata)
    {
        if (string.IsNullOrEmpty(annotation.Type) && annotation.UrlCitation is null)
        {
            return null;
        }

        var citationType = annotation.Type ?? string.Empty;
        if (citationType.Length == 0
            || (citationType == "url_citation" && HasOpenAiResponsesWebSearchCallMetadata(metadata)))
        {
            citationType = "web_search_result_location";
        }

        var citation = new AnthropicTextCitation { Type = citationType };
        if (annotation.UrlCitation is { } urlCitation)
        {
            citation.Url = urlCitation.Url ?? string.Empty;
            citation.Title = urlCitation.Title ?? string.Empty;
        }

        return citation;
    }

    /// <summary>Port of hasOpenAIResponsesWebSearchCallMetadata.</summary>
    public static bool HasOpenAiResponsesWebSearchCallMetadata(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return false;
        }

        if (!metadata.TryGetValue(OpenAIResponsesWebSearchCallsMetadata, out var calls)
            || calls.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > 0;
    }

    /// <summary>Port of attachCitationsToFirstAnthropicTextBlock.</summary>
    public static List<AnthropicMessageContentBlock> AttachCitationsToFirstAnthropicTextBlock(
        List<AnthropicMessageContentBlock> contentBlocks,
        List<Annotation>? annotations,
        Dictionary<string, JsonElement>? metadata)
    {
        if (annotations is null || annotations.Count == 0)
        {
            return contentBlocks;
        }

        var citations = new List<AnthropicTextCitation>(annotations.Count);
        foreach (var annotation in annotations)
        {
            if (CitationFromLlmAnnotation(annotation, metadata) is { } citation)
            {
                citations.Add(citation);
            }
        }

        if (citations.Count == 0)
        {
            return contentBlocks;
        }

        foreach (var block in contentBlocks)
        {
            if (block.Type != "text")
            {
                continue;
            }

            var existing = new HashSet<string>(
                (block.Citations ?? []).Select(AnthropicJson.CitationKey),
                StringComparer.Ordinal);
            block.Citations ??= [];
            foreach (var citation in citations)
            {
                if (existing.Add(AnthropicJson.CitationKey(citation)))
                {
                    block.Citations.Add(citation);
                }
            }

            return contentBlocks;
        }

        contentBlocks.Add(new AnthropicMessageContentBlock
        {
            Type = "text",
            Text = string.Empty,
            Citations = [.. citations],
        });

        return contentBlocks;
    }

    /// <summary>Port of mergeAnthropicResponseContentBlocks.</summary>
    public static List<AnthropicMessageContentBlock>? MergeAnthropicResponseContentBlocks(
        List<AnthropicMessageContentBlock> contentBlocks,
        Dictionary<string, JsonElement>? metadata,
        List<Annotation>? annotations)
    {
        var providerBlocks = GetAnthropicResponseContentFromMetadata(metadata);
        if (providerBlocks is null || providerBlocks.Count == 0)
        {
            return AttachCitationsToFirstAnthropicTextBlock(contentBlocks, annotations, metadata);
        }

        return AttachCitationsToFirstAnthropicTextBlock(providerBlocks, annotations, metadata);
    }

    /// <summary>Port of getAnthropicResponseContentFromMetadata.</summary>
    public static List<AnthropicMessageContentBlock>? GetAnthropicResponseContentFromMetadata(
        Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null
            || !metadata.TryGetValue(AnthropicJson.KeyResponseContent, out var raw)
            || raw.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            // The reference serializes the stored value and unmarshals it as a
            // block list; any failure yields nil.
            return raw.Deserialize<List<AnthropicMessageContentBlock>>(LlmJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Port of sortOrderedContentBlocks (stable sort).</summary>
    public static List<AnthropicMessageContentBlock> SortOrderedContentBlocks(
        List<OrderedContentBlock> blocks)
        => blocks
            .OrderBy(static block => block, OrderedContentBlockComparer.Instance)
            .Select(static block => block.Block)
            .ToList();

    /// <summary>Port of orderedContentBlock.</summary>
    public sealed record OrderedContentBlock(int Index, int Order, AnthropicMessageContentBlock Block);

    private sealed class OrderedContentBlockComparer : IComparer<OrderedContentBlock>
    {
        public static OrderedContentBlockComparer Instance { get; } = new();

        public int Compare(OrderedContentBlock? x, OrderedContentBlock? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);

            var aKnown = x.Index >= 0;
            var bKnown = y.Index >= 0;

            if (aKnown && bKnown)
            {
                return x.Index != y.Index
                    ? x.Index.CompareTo(y.Index)
                    : x.Order.CompareTo(y.Order);
            }

            if (aKnown != bKnown)
            {
                return aKnown ? -1 : 1;
            }

            return x.Order.CompareTo(y.Order);
        }
    }
}
