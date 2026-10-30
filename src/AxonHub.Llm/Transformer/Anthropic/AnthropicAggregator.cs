using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of anthropic.PlatformType for the aggregator's cache-token branches
/// (usage.go). Only the two behaviours the reference distinguishes are
/// modelled: Moonshot and everything else (direct / bedrock / vertex).
/// </summary>
public enum AnthropicPlatformType
{
    Direct,
    Moonshot,
}

/// <summary>
/// Port of llm/transformer/anthropic/aggregator.go: folds Anthropic streaming
/// chunks into one complete Messages response. Used when a non-streaming
/// client request had to stream upstream.
/// </summary>
public static class AnthropicAggregator
{
    /// <summary>Port of AggregateStreamChunks.</summary>
    public static byte[] AggregateStreamChunks(
        IReadOnlyList<StreamEvent> chunks,
        AnthropicPlatformType platformType,
        out ResponseMeta meta)
    {
        if (chunks.Count == 0)
        {
            throw new InvalidOperationException("empty stream chunks");
        }

        AnthropicStreamMessage? messageStart = null;
        var contentBlocks = new List<AnthropicMessageContentBlock>();
        AnthropicUsage? usage = null;
        string? stopReason = null;

        // Go accumulates input_json_delta bytes in the block's json.RawMessage;
        // an invalid fragment only fails later, when json.Marshal writes the
        // message. JsonElement cannot hold invalid JSON, so the port buffers the
        // text and materializes it after the loop (difference ledger).
        var rawToolInputs = new Dictionary<int, string>();

        foreach (var chunk in chunks)
        {
            AnthropicStreamEvent parsed;
            try
            {
                parsed = LlmJson.Deserialize<AnthropicStreamEvent>(chunk.Data)
                    ?? new AnthropicStreamEvent();
            }
            catch (JsonException)
            {
                continue; // Skip invalid chunks.
            }

            switch (parsed.Type)
            {
                case "message_start":
                    if (parsed.Message is not null)
                    {
                        messageStart = parsed.Message;
                        if (parsed.Message.Usage is not null)
                        {
                            usage = parsed.Message.Usage;
                        }
                    }

                    break;
                case "content_block_start":
                    if (parsed.ContentBlock is not null)
                    {
                        var block = parsed.ContentBlock;

                        // For tool-use-like blocks, reset Input so it is built
                        // from subsequent input_json_delta events.
                        if (AnthropicJson.IsAnthropicToolUseLike(block.Type))
                        {
                            block.Input = null;
                        }

                        // redacted_thinking blocks come complete in
                        // content_block_start with Data already populated;
                        // *_tool_result blocks arrive complete as well and are
                        // preserved as-is.
                        contentBlocks.Add(block);
                    }

                    break;
                case "content_block_delta":
                    if (parsed.Index is { } deltaIndex)
                    {
                        var index = (int)deltaIndex;
                        if (index < 0)
                        {
                            // The reference panics on a negative index; the port
                            // skips it (difference ledger).
                            break;
                        }

                        // Ensure we have enough content blocks.
                        while (contentBlocks.Count <= index)
                        {
                            contentBlocks.Add(new AnthropicMessageContentBlock
                            {
                                Type = "text",
                                Text = string.Empty,
                            });
                        }

                        if (parsed.Delta is { } delta)
                        {
                            ApplyTextDelta(contentBlocks[index], delta);
                            ApplyCitationDelta(contentBlocks[index], delta);
                            ApplyThinkingDelta(contentBlocks[index], delta);
                            ApplySignatureDelta(contentBlocks[index], delta);

                            if (delta.PartialJson is not null)
                            {
                                if (AnthropicJson.IsAnthropicToolUseLike(contentBlocks[index].Type))
                                {
                                    rawToolInputs[index] = rawToolInputs.TryGetValue(index, out var existing)
                                        ? existing + delta.PartialJson
                                        : delta.PartialJson;
                                }
                                else if (contentBlocks[index].Type == "text")
                                {
                                    contentBlocks[index].Text =
                                        (contentBlocks[index].Text ?? string.Empty) + delta.PartialJson;
                                }
                            }
                        }
                    }

                    break;
                case "message_delta":
                    if (parsed.Delta?.StopReason is { } reason)
                    {
                        stopReason = reason;
                    }

                    if (parsed.Usage is { } deltaUsage)
                    {
                        if (usage is null)
                        {
                            usage = deltaUsage;
                        }
                        else
                        {
                            // Merge usage from message_delta with message_start:
                            // keep input tokens from message_start, update output
                            // tokens from message_delta.
                            usage.OutputTokens = deltaUsage.OutputTokens;
                            if (deltaUsage.InputTokens > 0)
                            {
                                usage.InputTokens = deltaUsage.InputTokens;
                            }

                            if (deltaUsage.CachedTokens > 0)
                            {
                                usage.CachedTokens = deltaUsage.CachedTokens;
                                usage.InputTokens -= deltaUsage.CacheReadInputTokens;
                            }

                            if (deltaUsage.CacheCreationInputTokens > 0)
                            {
                                usage.CacheCreationInputTokens = deltaUsage.CacheCreationInputTokens;
                            }

                            if (deltaUsage.CacheReadInputTokens > 0)
                            {
                                usage.CacheReadInputTokens = deltaUsage.CacheReadInputTokens;
                            }
                        }
                    }

                    break;
                case "content_block_stop":
                    // Go copies the block, repairs an invalid input and never
                    // writes the copy back — a no-op. The final marshal still
                    // fails on invalid tool input, so the port omits the repair
                    // entirely (difference ledger).
                    break;
                case "message_stop":
                    // Final event, no additional processing needed.
                    break;
            }
        }

        ApplyRawToolInputs(contentBlocks, rawToolInputs);

        // Ensure we have at least one content block.
        if (contentBlocks.Count == 0)
        {
            contentBlocks.Add(new AnthropicMessageContentBlock
            {
                Type = "text",
                Text = string.Empty,
            });
        }

        // Create a default message when no message_start event was received.
        var message = messageStart is not null
            ? new AnthropicMessage
            {
                Id = messageStart.Id,
                Type = messageStart.Type,
                Role = messageStart.Role,
                Content = contentBlocks,
                Model = messageStart.Model,
                StopReason = stopReason,
                Usage = usage,
            }
            : new AnthropicMessage
            {
                Id = "msg_unknown",
                Type = "message",
                Role = "assistant",
                Content = contentBlocks,
                Model = "claude-3-sonnet-20240229",
                StopReason = stopReason,
                Usage = usage,
            };

        byte[] data;
        try
        {
            data = GoJson.Marshal(message);
        }
        catch (JsonException ex)
        {
            meta = new ResponseMeta();
            throw new InvalidOperationException(ex.Message, ex);
        }

        var completed = messageStart is not null && !string.IsNullOrEmpty(stopReason);
        meta = usage is not null
            ? new ResponseMeta
            {
                Id = message.Id,
                Usage = ConvertToLlmUsage(usage, platformType),
                Completed = completed,
            }
            : new ResponseMeta
            {
                Id = message.Id,
                Completed = completed,
            };

        return data;
    }

    /// <summary>
    /// Port of convertToLlmUsage (usage.go): mutates <paramref name="usage"/>
    /// the way the reference does (the moonshot cached-token fix-up), so call it
    /// only after the response body has been marshalled.
    /// </summary>
    public static Usage? ConvertToLlmUsage(AnthropicUsage? usage, AnthropicPlatformType platformType)
    {
        if (usage is null)
        {
            return null;
        }

        // Handle moonshot's cached_tokens field.
        if (usage.CachedTokens > 0 && usage.CacheCreationInputTokens == 0)
        {
            usage.CacheReadInputTokens = usage.CachedTokens;
        }

        long promptTokens;

        if (platformType == AnthropicPlatformType.Moonshot)
        {
            // Moonshot may return InputTokens as a net billed amount (can be
            // negative with cache discounts), or as the standard positive count.
            if (usage.InputTokens < 0 && usage.CacheReadInputTokens > 0)
            {
                promptTokens = usage.InputTokens + (2 * usage.CacheReadInputTokens);
            }
            else if (usage.InputTokens >= 0
                     && usage.CacheReadInputTokens > 0
                     && usage.InputTokens < usage.CacheReadInputTokens)
            {
                promptTokens = usage.InputTokens + usage.CacheReadInputTokens;
            }
            else
            {
                promptTokens = usage.InputTokens;
            }
        }
        else
        {
            // For Anthropic official (direct, bedrock, vertex) or other
            // platforms, input_tokens does NOT include cached tokens.
            promptTokens = usage.InputTokens + usage.CacheCreationInputTokens + usage.CacheReadInputTokens;
        }

        var llmUsage = new Usage
        {
            PromptTokens = promptTokens,
            CompletionTokens = usage.OutputTokens,
            CompletionTokensDetails = new CompletionTokensDetails(),
            TotalTokens = promptTokens + usage.OutputTokens,
        };

        if (usage.CacheReadInputTokens > 0
            || usage.CacheCreationInputTokens > 0
            || usage.CacheCreation.Ephemeral5mInputTokens > 0
            || usage.CacheCreation.Ephemeral1hInputTokens > 0)
        {
            llmUsage.PromptTokensDetails = new PromptTokensDetails
            {
                CachedTokens = usage.CacheReadInputTokens,
                WriteCachedTokens = usage.CacheCreationInputTokens,
                WriteCached5MinTokens = usage.CacheCreation.Ephemeral5mInputTokens,
                WriteCached1HourTokens = usage.CacheCreation.Ephemeral1hInputTokens,
            };
        }

        return llmUsage;
    }

    private static void ApplyTextDelta(AnthropicMessageContentBlock block, AnthropicStreamDelta delta)
    {
        if (delta.Text is null || block.Type != "text")
        {
            return;
        }

        block.Text = (block.Text ?? string.Empty) + delta.Text;
    }

    private static void ApplyCitationDelta(AnthropicMessageContentBlock block, AnthropicStreamDelta delta)
    {
        if (delta.Citation is null || block.Type != "text")
        {
            return;
        }

        block.Citations ??= [];
        block.Citations.Add(delta.Citation);
    }

    private static void ApplyThinkingDelta(AnthropicMessageContentBlock block, AnthropicStreamDelta delta)
    {
        if (delta.Thinking is null)
        {
            return;
        }

        if (block.Type == "thinking")
        {
            block.Thinking = (block.Thinking ?? string.Empty) + delta.Thinking;
            return;
        }

        // Convert to a thinking block if it is not already one.
        block.Type = "thinking";
        block.Thinking = delta.Thinking;
    }

    private static void ApplySignatureDelta(AnthropicMessageContentBlock block, AnthropicStreamDelta delta)
    {
        if (delta.Signature is null)
        {
            return;
        }

        if (block.Type == "thinking")
        {
            block.Signature = block.Signature is null
                ? delta.Signature
                : block.Signature + delta.Signature;
            return;
        }

        // Convert to a thinking block if it is not already one.
        block.Type = "thinking";
        block.Signature = delta.Signature;
    }

    /// <summary>
    /// Materializes the buffered input_json_delta fragments into
    /// <see cref="AnthropicMessageContentBlock.Input"/>. An empty buffer stays
    /// null (Go's <c>omitempty</c> on a zero-length RawMessage), and invalid
    /// JSON fails here exactly where Go's json.Marshal would.
    /// </summary>
    private static void ApplyRawToolInputs(
        List<AnthropicMessageContentBlock> contentBlocks,
        Dictionary<int, string> rawToolInputs)
    {
        foreach (var (index, raw) in rawToolInputs)
        {
            if (index >= contentBlocks.Count || raw.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                contentBlocks[index].Input = GoJson.CompactElement(document.RootElement);
            }
            catch (JsonException ex)
            {
                // Go's json.Marshal reports the same condition (an invalid
                // json.RawMessage); the wording differs (difference ledger).
                throw new InvalidOperationException(ex.Message, ex);
            }
        }
    }
}
