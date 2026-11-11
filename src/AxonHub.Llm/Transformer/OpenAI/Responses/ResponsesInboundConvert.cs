using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of llm/transformer/openai/responses/inbound.go and cache_anchor.go
/// conversion helpers: Responses API request/response data and the unified
/// model, plus the namespace encoding applied at the request boundary.
/// </summary>
public static class ResponsesInboundConvert
{
    /// <summary>
    /// Go: <c>convertToLLMRequest</c>. <paramref name="rawBody"/> is the raw
    /// request body used to retain fields the unified model cannot represent.
    /// </summary>
    public static LlmRequest ConvertToLlmRequest(ResponsesRequest req, byte[]? rawBody = null)
    {
        var chatReq = new LlmRequest
        {
            Model = req.Model,
            Temperature = req.Temperature,
            Stream = req.Stream,
            Metadata = req.Metadata is null
                ? null
                : new Dictionary<string, string>(req.Metadata, StringComparer.Ordinal),
            RequestType = RequestTypes.Chat,
            ApiFormat = ApiFormats.OpenAIResponse,
            MaxCompletionTokens = req.MaxOutputTokens,
            User = req.User,
            Store = req.Store,
            TopLogprobs = req.TopLogprobs,
            TopP = req.TopP,
            SafetyIdentifier = req.SafetyIdentifier,
            ServiceTier = req.ServiceTier,
            ParallelToolCalls = req.ParallelToolCalls,
            PromptCacheKey = req.PromptCacheKey,
            PreviousResponseId = req.PreviousResponseId,
            TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            TransformOptions = new TransformOptions(),
        };

        // Store help fields in TransformerMetadata.
        if (req.Include is { Count: > 0 } include)
        {
            chatReq.TransformerMetadata["include"] = ResponsesConvertUtil.ToElement(include);
        }

        if (req.MaxToolCalls is { } maxToolCalls)
        {
            chatReq.TransformerMetadata["max_tool_calls"] = ResponsesConvertUtil.ToElement(maxToolCalls);
        }

        if (req.PromptCacheRetention is { } promptCacheRetention)
        {
            chatReq.TransformerMetadata["prompt_cache_retention"] = ResponsesConvertUtil.ToElement(promptCacheRetention);
        }

        if (req.Truncation is { } truncation)
        {
            chatReq.TransformerMetadata["truncation"] = ResponsesConvertUtil.ToElement(truncation);
        }

        // Convert reasoning.
        if (req.Reasoning is { } reasoning)
        {
            if (!string.IsNullOrEmpty(reasoning.Effort))
            {
                chatReq.ReasoningEffort = reasoning.Effort;
            }

            if (reasoning.MaxTokens is { } maxTokens)
            {
                chatReq.ReasoningBudget = maxTokens;
            }

            // Priority: summary > generate_summary.
            if (!string.IsNullOrEmpty(reasoning.Summary))
            {
                chatReq.ReasoningSummary = reasoning.Summary;
            }
            else if (!string.IsNullOrEmpty(reasoning.GenerateSummary))
            {
                chatReq.ReasoningSummary = reasoning.GenerateSummary;
            }
        }

        // Convert stream options.
        if (req.StreamOptions is { } streamOptions)
        {
            chatReq.StreamOptions = new StreamOptions();
            if (streamOptions.IncludeObfuscation is { } includeObfuscation)
            {
                chatReq.TransformerMetadata["include_obfuscation"] = ResponsesConvertUtil.ToElement(includeObfuscation);
            }
        }

        if (req.Tools is { Count: > 0 } tools)
        {
            chatReq.Tools = ConvertToolsToLlm(tools);
        }

        // Convert instructions to system message.
        var messages = new List<Message>();
        if (!string.IsNullOrEmpty(req.Instructions))
        {
            messages.Add(new Message
            {
                Role = "system",
                Content = new MessageContent { Content = req.Instructions },
            });
        }

        // Decode native tool identities before normalizing their names below.
        if (req.Input?.Items is not null)
        {
            chatReq.TransformOptions.ArrayInputs = true;
        }

        if (ConvertInputToMessages(req.Input) is { } inputMessages)
        {
            messages.AddRange(inputMessages);
        }

        chatReq.Messages = messages;

        // Preserve tool selection independently of namespace declaration normalization.
        chatReq.ToolChoice = ConvertToolChoiceToLlm(req.ToolChoice);

        // Convert text format to response format.
        if (req.Text?.Format is { } format && !string.IsNullOrEmpty(format.Type))
        {
            chatReq.ResponseFormat = new ResponseFormat { Type = format.Type };

            // Reconstruct json_schema from TextFormat fields.
            if (format.Type == "json_schema" && !string.IsNullOrEmpty(format.Name))
            {
                var jsonSchema = new ResponsesRawJsonSchema
                {
                    Name = format.Name,
                    Description = ResponsesConvertUtil.NullIfEmpty(format.Description),
                    Schema = format.Schema is { } schema ? GoJson.CompactElement(schema) : null,
                    Strict = format.Strict,
                };

                chatReq.ResponseFormat.JsonSchema = ResponsesConvertUtil.Parse(GoJson.Marshal(jsonSchema));
            }
        }

        // Convert text verbosity.
        if (req.Text is { } text)
        {
            chatReq.Verbosity = text.Verbosity;
        }

        if (rawBody is not null)
        {
            ResponsesRequestExtensions.AttachOpenAIResponsesRequestExtensions(chatReq, req, rawBody);
        }

        var result = ResponsesNamespace.FlattenRequestFunctionNames(chatReq);
        result.TransformerMetadata = ResponsesNamespace.NamespaceMetadata(result);
        return result;
    }

    /// <summary>
    /// Go: <c>convertToolChoiceToLLM</c>. Preserves Responses tool selection
    /// without imposing Chat Completions naming or selection restrictions.
    /// </summary>
    public static ToolChoice? ConvertToolChoiceToLlm(ResponsesToolChoice? src)
    {
        if (src is null)
        {
            return null;
        }

        var result = new ToolChoice { ToolChoiceMode = src.Mode };
        if (src.Type is not null)
        {
            result.NamedToolChoice = new NamedToolChoice
            {
                Type = src.Type,
                Function = new ToolFunction { Name = src.Name ?? string.Empty },
            };
        }

        if (src.Tools is { } options)
        {
            foreach (var option in options)
            {
                result.Tools ??= [];
                result.Tools.Add(new ToolOption { Type = option.Type, Name = option.Name });
            }
        }

        return result;
    }

    /// <summary>
    /// Go: <c>convertInputToMessages</c>. Merges consecutive tool calls while
    /// preserving native local function names; request and response boundaries
    /// encode names when entering the unified model.
    /// </summary>
    public static List<Message>? ConvertInputToMessages(ResponsesInput? input)
    {
        if (input is null)
        {
            return null;
        }

        // If input is a simple text string.
        if (input.Text is not null)
        {
            return
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = input.Text },
                },
            ];
        }

        // If input is an array of items.
        var items = input.Items;
        var messages = new List<Message>(items?.Count ?? 0);
        var i = 0;

        while (items is not null && i < items.Count)
        {
            var item = items[i];

            // Handle reasoning item - merge with subsequent function_call or text items.
            if (item.Type == "reasoning")
            {
                var (msg, consumed) = ConvertReasoningWithFollowing(items, i);
                if (msg is not null)
                {
                    messages.Add(msg);
                }

                i += consumed;
                continue;
            }

            if (item.Type is "function_call" or "custom_tool_call")
            {
                var msg = new Message { Role = "assistant" };

                while (i < items.Count)
                {
                    var callItem = items[i];
                    if (callItem.Type is not ("function_call" or "custom_tool_call"))
                    {
                        break;
                    }

                    var callMsg = ConvertItemToMessage(callItem);
                    if (callMsg?.ToolCalls is { } calls)
                    {
                        msg.ToolCalls ??= [];
                        msg.ToolCalls.AddRange(calls);
                    }

                    i++;
                }

                messages.Add(msg);
                continue;
            }

            // Handle regular items.
            var regular = ConvertItemToMessage(item);
            if (regular is not null)
            {
                messages.Add(regular);
            }

            i++;
        }

        return messages;
    }

    /// <summary>
    /// Go: <c>convertReasoningWithFollowing</c>. Converts a reasoning item and
    /// merges it with subsequent function_call items or text content into a
    /// single assistant message. Returns the merged message and the number of
    /// items consumed.
    /// </summary>
    public static (Message? Message, int Consumed) ConvertReasoningWithFollowing(List<ResponsesItem> items, int startIdx)
    {
        if (startIdx >= items.Count || items[startIdx].Type != "reasoning")
        {
            return (null, 0);
        }

        var msg = new Message { Role = "assistant" };
        var consumed = 0;

        // Collect all consecutive reasoning items before looking for the
        // assistant content or tool call they belong to. Each item keeps its
        // own ID, summary, and opaque encrypted content.
        for (var i = startIdx; i < items.Count && items[i].Type == "reasoning"; i++)
        {
            var reasoningItem = items[i];
            var reasoningText = new StringBuilder();
            if (reasoningItem.Summary is { } summaries)
            {
                foreach (var summary in summaries)
                {
                    reasoningText.Append(summary.Text);
                }
            }

            msg.ReasoningItems ??= [];
            msg.ReasoningItems.Add(new ReasoningItem
            {
                Id = ResponsesConvertUtil.NullIfEmpty(reasoningItem.Id),
                Content = ResponsesConvertUtil.NullIfEmpty(reasoningText.ToString()),
                Signature = ResponsesConvertUtil.NullIfEmpty(reasoningItem.EncryptedContent),
            });
            consumed++;
        }

        // Keep scalar fallbacks for Chat-compatible upstreams, which do not
        // consume ReasoningItems. The item slice remains authoritative for
        // Responses replay.
        var aggregateReasoning = new StringBuilder();
        foreach (var reasoningItem in msg.ReasoningItems!)
        {
            aggregateReasoning.Append(reasoningItem.Content);
        }

        if (aggregateReasoning.Length > 0)
        {
            msg.ReasoningContent = aggregateReasoning.ToString();
        }

        var signature = msg.ReasoningItems[^1].Signature;
        if (!string.IsNullOrEmpty(signature))
        {
            msg.ReasoningSignature = signature;
        }

        // Look ahead for subsequent function_call items to merge.
        for (var i = startIdx + consumed; i < items.Count; i++)
        {
            var nextItem = items[i];

            switch (nextItem.Type)
            {
                case "function_call":
                    // Merge function_call into the same assistant message.
                    msg.ToolCalls ??= [];
                    msg.ToolCalls.Add(new ToolCall
                    {
                        Id = ResponsesConvertUtil.NullIfEmpty(nextItem.CallId),
                        Type = ToolTypes.Function,
                        Function = new FunctionCall
                        {
                            Name = nextItem.Name,
                            Namespace = ResponsesConvertUtil.NullIfEmpty(nextItem.Namespace),
                            Arguments = nextItem.Arguments,
                        },
                    });
                    consumed++;
                    break;

                case "custom_tool_call":
                    // Merge custom_tool_call into the same assistant message.
                    msg.ToolCalls ??= [];
                    msg.ToolCalls.Add(new ToolCall
                    {
                        Id = ResponsesConvertUtil.NullIfEmpty(nextItem.CallId),
                        Type = ToolTypes.ResponsesCustomTool,
                        ResponseCustomToolCall = new ResponseCustomToolCall
                        {
                            CallId = nextItem.CallId,
                            Name = nextItem.Name,
                            Input = nextItem.Input ?? string.Empty,
                        },
                    });
                    consumed++;
                    break;

                case "message":
                case "input_text":
                case "":
                    // If we encounter a text message with assistant role, merge its content.
                    if (nextItem.Role == "assistant")
                    {
                        msg.Id = ResponsesConvertUtil.NullIfEmpty(nextItem.Id);
                        if (nextItem.Content is { Items.Count: > 0 } && nextItem.IsOutputMessageContent())
                        {
                            msg.Content = ConvertContentItemsToMessageContent(nextItem.GetContentItems()!);
                        }
                        else if (nextItem.Content is { } content)
                        {
                            msg.Content = ConvertToMessageContent(content);
                        }
                        else if (nextItem.Text is { } text)
                        {
                            msg.Content = new MessageContent { Content = text };
                        }

                        consumed++;
                    }
                    else
                    {
                        // Non-assistant message, stop merging.
                        return (msg, consumed);
                    }

                    break;

                default:
                    // Any other type (including function_call_output), stop merging.
                    return (msg, consumed);
            }
        }

        return (msg, consumed);
    }

    /// <summary>Go: <c>convertItemToMessage</c>.</summary>
    public static Message? ConvertItemToMessage(ResponsesItem? item)
    {
        if (item is null)
        {
            return null;
        }

        switch (item.Type)
        {
            case "message":
            case "input_text":
            case "":
                {
                    var msg = new Message
                    {
                        Id = ResponsesConvertUtil.NullIfEmpty(item.Id),
                        Role = ResponsesConvertUtil.NullIfEmpty(item.Role),
                    };

                    // Handle content - check Content.Items first (output message format from JSON).
                    if (item.Content is { Items.Count: > 0 } && item.IsOutputMessageContent())
                    {
                        msg.Content = ConvertContentItemsToMessageContent(item.GetContentItems()!);
                    }
                    else if (item.Content is { } content)
                    {
                        msg.Content = ConvertToMessageContent(content);
                    }
                    else if (item.Text is { } text)
                    {
                        msg.Content = new MessageContent { Content = text };
                    }

                    return msg;
                }

            case "input_image":
                // Input image as a standalone item.
                if (item.ImageUrl is { } imageUrl)
                {
                    return new Message
                    {
                        Role = item.Role.Length > 0 ? item.Role : "user",
                        Content = new MessageContent
                        {
                            MultipleContent =
                            [
                                new MessageContentPart
                                {
                                    Type = "image_url",
                                    ImageUrl = new ImageUrl { Url = imageUrl, Detail = item.Detail },
                                },
                            ],
                        },
                    };
                }

                return null;

            case "input_file":
                return ResponseInputFileMessage(item);

            case "function_call":
                // Function call from assistant - convert to tool call.
                return new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = ResponsesConvertUtil.NullIfEmpty(item.CallId),
                            Type = ToolTypes.Function,
                            Function = new FunctionCall
                            {
                                Name = item.Name,
                                Namespace = ResponsesConvertUtil.NullIfEmpty(item.Namespace),
                                Arguments = item.Arguments,
                            },
                        },
                    ],
                };

            case "custom_tool_call":
                // Custom tool call from assistant - convert to tool call with ResponseCustomToolCall.
                return new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = ResponsesConvertUtil.NullIfEmpty(item.CallId),
                            Type = ToolTypes.ResponsesCustomTool,
                            ResponseCustomToolCall = new ResponseCustomToolCall
                            {
                                CallId = item.CallId,
                                Name = item.Name,
                                Input = item.Input ?? string.Empty,
                            },
                        },
                    ],
                };

            case "function_call_output":
                if (item.Output is null)
                {
                    throw TransformerErrors.InvalidRequest(
                        "function_call_output item must have non-nil Output");
                }

                return ToolOutputMessage(item);

            case "custom_tool_call_output":
                if (item.Output is null)
                {
                    throw TransformerErrors.InvalidRequest(
                        "custom_tool_call_output item must have non-nil Output");
                }

                return ToolOutputMessage(item);

            case "reasoning":
                // Reasoning is handled by ConvertReasoningWithFollowing in
                // ConvertInputToMessages; skip it if reached directly.
                return null;

            case "compaction":
            case "compaction_summary":
                return ResponsesCompactConvert.CompactionMessageFromItem(item, item.Type);

            default:
                // Skip unknown types.
                return null;
        }
    }

    private static Message ToolOutputMessage(ResponsesItem item)
    {
        var msg = new Message
        {
            Role = "tool",
            ToolCallId = item.CallId,
            Content = ConvertToMessageContent(item.Output!),
        };
        if (!string.IsNullOrEmpty(item.Name))
        {
            msg.ToolCallName = item.Name;
        }

        return msg;
    }

    /// <summary>Go: <c>convertToMessageContent</c>.</summary>
    public static MessageContent ConvertToMessageContent(ResponsesInput content)
    {
        var parts = ConvertToMessageContentParts(content);

        // If only one text item, return simple Content.
        if (parts.Count == 1
            && parts[0].Type is "text" or "input_text"
            && parts[0].Text is { } text)
        {
            return new MessageContent { Content = text };
        }

        return new MessageContent { MultipleContent = parts };
    }

    /// <summary>Go: <c>convertContentItemsToMessageContent</c>.</summary>
    public static MessageContent ConvertContentItemsToMessageContent(List<ResponsesContentItem> items)
    {
        // If only one text item, return simple Content.
        if (items.Count == 1 && items[0].Type is "output_text" or "input_text" or "text")
        {
            return new MessageContent { Content = items[0].Text ?? string.Empty };
        }

        // Convert to MultipleContent.
        var parts = new List<MessageContentPart>(items.Count);
        foreach (var item in items)
        {
            if (item.Type is "output_text" or "input_text" or "text")
            {
                parts.Add(new MessageContentPart
                {
                    Type = "text",
                    Text = item.Text ?? string.Empty,
                });
            }
        }

        return new MessageContent { MultipleContent = parts };
    }

    /// <summary>Go: <c>convertToMessageContentParts</c>.</summary>
    public static List<MessageContentPart> ConvertToMessageContentParts(ResponsesInput input)
    {
        if (input.Text is not null)
        {
            return
            [
                new MessageContentPart
                {
                    Type = "input_text",
                    Text = input.Text,
                },
            ];
        }

        var parts = new List<MessageContentPart>(input.Items?.Count ?? 0);
        if (input.Items is { } items)
        {
            foreach (var item in items)
            {
                var part = ConvertContentItemToPart(item);
                if (part is not null)
                {
                    parts.Add(part);
                }
            }
        }

        return parts;
    }

    /// <summary>Go: <c>convertContentItemToPart</c>.</summary>
    public static MessageContentPart? ConvertContentItemToPart(ResponsesItem? item)
    {
        if (item is null)
        {
            return null;
        }

        switch (item.Type)
        {
            case "input_text":
            case "text":
            case "output_text":
                if (item.Text is { } text)
                {
                    return new MessageContentPart
                    {
                        Id = ResponsesConvertUtil.NullIfEmpty(item.Id),
                        Type = "text",
                        Text = text,
                    };
                }

                return null;

            case "input_image":
                if (item.ImageUrl is { } imageUrl)
                {
                    return new MessageContentPart
                    {
                        Id = ResponsesConvertUtil.NullIfEmpty(item.Id),
                        Type = "image_url",
                        ImageUrl = new ImageUrl { Url = imageUrl, Detail = item.Detail },
                    };
                }

                return null;

            case "input_file":
                {
                    var message = ResponseInputFileMessage(item);
                    if (message?.Content?.MultipleContent is not { Count: > 0 } parts)
                    {
                        return null;
                    }

                    return parts[0];
                }

            case "compaction":
            case "compaction_summary":
                return ResponsesCompactConvert.CompactionContentPartFromItem(item, item.Type);

            default:
                return null;
        }
    }

    /// <summary>Go: <c>responseInputFileMessage</c>.</summary>
    public static Message? ResponseInputFileMessage(ResponsesItem item)
    {
        var document = new DocumentUrl
        {
            FileId = item.FileId,
            Filename = item.Filename,
        };

        if (item.FileData is not null)
        {
            document.Url = item.FileData;
        }
        else if (item.FileUrl is not null)
        {
            document.Url = item.FileUrl;
        }

        if (Xurl.ParseDataUrl(document.Url) is { } parsed)
        {
            document.MimeType = parsed.MediaType;
        }

        if (document.Url.Length == 0 && string.IsNullOrEmpty(document.FileId))
        {
            return null;
        }

        return new Message
        {
            Role = item.Role.Length > 0 ? item.Role : "user",
            Content = new MessageContent
            {
                MultipleContent =
                [
                    new MessageContentPart
                    {
                        Id = ResponsesConvertUtil.NullIfEmpty(item.Id),
                        Type = "document",
                        Document = document,
                    },
                ],
            },
        };
    }

    /// <summary>Go: <c>convertToolsToLLM</c>.</summary>
    public static List<Tool> ConvertToolsToLlm(List<ResponsesTool> tools)
    {
        var result = new List<Tool>(tools.Count);

        foreach (var tool in tools)
        {
            switch (tool.Type)
            {
                case "function":
                    result.Add(new Tool
                    {
                        Type = ToolTypes.Function,
                        Function = new Function
                        {
                            Name = tool.Name ?? string.Empty,
                            Description = responsesToolDescription(tool),
                            Parameters = MarshalParameters(tool.Parameters, "function parameters"),
                            Strict = tool.Strict,
                        },
                    });
                    break;

                case "image_generation":
                    result.Add(new Tool
                    {
                        Type = ToolTypes.ImageGeneration,
                        ImageGeneration = new ImageGeneration
                        {
                            Background = ResponsesConvertUtil.NullIfEmpty(tool.Background),
                            InputFidelity = ResponsesConvertUtil.NullIfEmpty(tool.InputFidelity),
                            Moderation = ResponsesConvertUtil.NullIfEmpty(tool.Moderation),
                            OutputCompression = tool.OutputCompression,
                            OutputFormat = ResponsesConvertUtil.NullIfEmpty(tool.OutputFormat),
                            PartialImages = tool.PartialImages,
                            Quality = ResponsesConvertUtil.NullIfEmpty(tool.Quality),
                            Size = ResponsesConvertUtil.NullIfEmpty(tool.Size),
                        },
                    });
                    break;

                case "web_search":
                    {
                        var webSearch = new WebSearch();
                        // Go: append(nil, empty...) stays nil.
                        if (tool.Filters?.AllowedDomains is { Count: > 0 } allowedDomains)
                        {
                            webSearch.AllowedDomains = [.. allowedDomains];
                        }

                        if (tool.UserLocation is { } location)
                        {
                            var locationType = location.Type;
                            if (string.IsNullOrEmpty(locationType))
                            {
                                locationType = "approximate";
                            }

                            webSearch.UserLocation = new WebSearchToolUserLocation
                            {
                                Type = locationType,
                                City = ResponsesConvertUtil.NullIfEmpty(location.City),
                                Country = ResponsesConvertUtil.NullIfEmpty(location.Country),
                                Region = ResponsesConvertUtil.NullIfEmpty(location.Region),
                                Timezone = ResponsesConvertUtil.NullIfEmpty(location.Timezone),
                            };
                        }

                        result.Add(new Tool
                        {
                            Type = ToolTypes.WebSearch,
                            WebSearch = webSearch,
                        });
                        break;
                    }

                case "custom":
                    {
                        var customTool = new ResponseCustomTool
                        {
                            Name = tool.Name ?? string.Empty,
                            Description = ResponsesConvertUtil.NullIfEmpty(tool.Description),
                        };
                        if (tool.Format is { } format)
                        {
                            customTool.Format = new ResponseCustomToolFormat
                            {
                                Type = format.Type,
                                Syntax = ResponsesConvertUtil.NullIfEmpty(format.Syntax),
                                Definition = ResponsesConvertUtil.NullIfEmpty(format.Definition),
                            };
                        }

                        result.Add(new Tool
                        {
                            Type = ToolTypes.ResponsesCustomTool,
                            ResponseCustomTool = customTool,
                        });
                        break;
                    }

                case "namespace":
                    if (tool.Tools is { } subTools)
                    {
                        foreach (var subTool in subTools)
                        {
                            if (subTool.Type != "function")
                            {
                                continue;
                            }

                            result.Add(new Tool
                            {
                                Type = ToolTypes.Function,
                                Function = new Function
                                {
                                    Name = subTool.Name ?? string.Empty,
                                    Namespace = tool.Name,
                                    Description = responsesToolDescription(subTool),
                                    Parameters = MarshalParameters(
                                        subTool.Parameters,
                                        "namespace tool parameters"),
                                    Strict = subTool.Strict,
                                },
                            });
                        }
                    }

                    break;

                default:
                    // Skip unsupported tool types.
                    break;
            }
        }

        return result;
    }

    /// <summary>Go: <c>tool.Description</c> is a plain string; empty is its zero value.</summary>
    private static string? responsesToolDescription(ResponsesTool tool)
        => ResponsesConvertUtil.NullIfEmpty(tool.Description);

    /// <summary>
    /// Go: <c>json.Marshal(tool.Parameters)</c> into <c>llm.Function.Parameters</c>.
    /// A nil map marshals as the literal <c>null</c>.
    /// </summary>
    private static JsonElement MarshalParameters(Dictionary<string, JsonElement>? parameters, string what)
    {
        try
        {
            return parameters is null
                ? ResponsesConvertUtil.NullElement()
                : ResponsesConvertUtil.Parse(GoJson.Marshal(parameters));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("failed to marshal " + what + ": " + ex.Message, ex);
        }
    }

    /// <summary>Go: <c>getResponseWebSearchCallsFromMetadata</c>.</summary>
    public static List<ResponsesItem>? GetResponseWebSearchCallsFromMetadata(
        Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is not { Count: > 0 })
        {
            return null;
        }

        if (!metadata.TryGetValue(ResponsesKeys.WebSearchCallsTransformerMetadataKey, out var raw)
            || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        List<ResponsesItem>? items;
        try
        {
            // Go accepts an in-process []Item or JSON-decodes the stored value;
            // the port always stores the decoded JSON form.
            items = raw.Deserialize<List<ResponsesItem>>(LlmJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (items is null)
        {
            return null;
        }

        var result = new List<ResponsesItem>(items.Count);
        foreach (var item in items)
        {
            if (item.Type != "web_search_call" || item.Action?.WebSearch is not { } src)
            {
                continue;
            }

            result.Add(new ResponsesItem
            {
                Id = item.Id,
                Type = item.Type,
                Status = item.Status,
                Action = ResponsesItemAction.NewWebSearchAction(new ResponsesWebSearchAction
                {
                    Type = src.Type,
                    Query = src.Query,
                    // Go: append(nil, empty...) yields nil.
                    Queries = src.Queries is { Count: > 0 } ? [.. src.Queries] : null,
                    Sources = src.Sources is { Count: > 0 } ? [.. src.Sources] : null,
                }),
            });
        }

        return result;
    }

    /// <summary>Go: <c>attachAnnotationsToFirstTextItem</c>.</summary>
    public static (List<ResponsesItem> Items, bool Attached) AttachAnnotationsToFirstTextItem(
        List<ResponsesItem> items, List<Annotation>? annotations)
    {
        if (items.Count == 0 || annotations is not { Count: > 0 })
        {
            return (items, false);
        }

        var firstTextItemIdx = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Type is "output_text" or "input_text" or "text")
            {
                firstTextItemIdx = i;
                break;
            }
        }

        if (firstTextItemIdx < 0)
        {
            return (items, false);
        }

        var mapped = new List<ResponsesAnnotation>(annotations.Count);
        foreach (var annotation in annotations)
        {
            var result = new ResponsesAnnotation
            {
                Type = ResponsesConvertUtil.NullIfEmpty(annotation.Type),
                StartIndex = annotation.StartIndex,
                EndIndex = annotation.EndIndex,
            };

            if (annotation.UrlCitation is { } citation)
            {
                result.UrlCitation = new ResponsesUrlCitation
                {
                    Url = ResponsesConvertUtil.NullIfEmpty(citation.Url),
                    Title = ResponsesConvertUtil.NullIfEmpty(citation.Title),
                };
            }

            mapped.Add(result);
        }

        items[firstTextItemIdx].Annotations = mapped;
        return (items, true);
    }

    /// <summary>Go: <c>convertToResponsesAPIResponse</c>.</summary>
    public static ResponsesResponse ConvertToResponsesApiResponse(LlmResponse chatResp)
    {
        // Go: append([]Item(nil), ...) keeps nil when there are no web-search calls.
        var output = GetResponseWebSearchCallsFromMetadata(chatResp.TransformerMetadata) is { Count: > 0 } webCalls
            ? new List<ResponsesItem>(webCalls)
            : null;

        void AddOutput(ResponsesItem item)
        {
            output ??= [];
            output.Add(item);
        }

        var resp = new ResponsesResponse
        {
            Object = "response",
            Id = chatResp.Id,
            Model = chatResp.Model,
            CreatedAt = chatResp.Created,
            Output = output,
            Status = "completed",
            PreviousResponseId = chatResp.PreviousResponseId,
        };

        // Convert usage.
        resp.Usage = ResponsesUsage.ConvertLlmUsageToResponsesUsage(chatResp.Usage);

        // Convert choices to output items.
        foreach (var choice in chatResp.Choices)
        {
            var message = choice.Message ?? choice.Delta;
            if (message is null)
            {
                continue;
            }

            var messageItemId = message.Id;
            if (string.IsNullOrEmpty(messageItemId))
            {
                messageItemId = GenerateItemId();
            }

            // Handle reasoning content. A message may carry multiple
            // independently signed reasoning items, each of which must remain a
            // separate Responses output item for a later tool-result request.
            foreach (var reasoningItem in BuildReasoningItems(message))
            {
                AddOutput(reasoningItem);
            }

            // Handle tool calls (function calls and custom tool calls).
            if (message.ToolCalls is { Count: > 0 } toolCalls)
            {
                foreach (var toolCall in toolCalls)
                {
                    if (toolCall.ResponseCustomToolCall is { } custom)
                    {
                        AddOutput(new ResponsesItem
                        {
                            Id = toolCall.Id ?? string.Empty,
                            Type = "custom_tool_call",
                            CallId = custom.CallId,
                            Name = custom.Name,
                            Input = custom.Input,
                            Status = "completed",
                        });
                    }
                    else
                    {
                        AddOutput(new ResponsesItem
                        {
                            Id = toolCall.Id ?? string.Empty,
                            Type = "function_call",
                            CallId = toolCall.Id ?? string.Empty,
                            Name = toolCall.Function.Name,
                            Namespace = toolCall.Function.Namespace ?? string.Empty,
                            Arguments = toolCall.Function.Arguments,
                            Status = "completed",
                        });
                    }
                }
            }

            // Handle text content.
            if (message.Content?.Content is { Length: > 0 } text)
            {
                var contentItems = new List<ResponsesItem>
                {
                    new()
                    {
                        Type = "output_text",
                        Text = text,
                        Annotations = [],
                    },
                };
                (contentItems, _) = AttachAnnotationsToFirstTextItem(contentItems, message.Annotations);
                AddOutput(new ResponsesItem
                {
                    Id = messageItemId,
                    Type = "message",
                    Role = "assistant",
                    Content = new ResponsesInput { Items = contentItems },
                    Status = "completed",
                });
            }
            else if (message.Content?.MultipleContent is { Count: > 0 } parts)
            {
                var contentItems = new List<ResponsesItem>();

                foreach (var part in parts)
                {
                    switch (part.Type)
                    {
                        case "text":
                            if (part.Text is { } partText)
                            {
                                contentItems.Add(new ResponsesItem
                                {
                                    Type = "output_text",
                                    Text = partText,
                                    Annotations = [],
                                });
                            }

                            break;

                        case "image_url":
                            // Handle image output.
                            if (part.ImageUrl is { } imageUrl)
                            {
                                AddOutput(new ResponsesItem
                                {
                                    Id = GenerateItemId(),
                                    Type = "image_generation_call",
                                    Role = "assistant",
                                    Result = Xurl.ExtractBase64FromDataUrl(imageUrl.Url),
                                    Status = "completed",
                                    Background = ResponsesMetadata.GetStringPtr(
                                        part.TransformerMetadata, "background"),
                                    OutputFormat = ResponsesMetadata.GetStringPtr(
                                        part.TransformerMetadata, "output_format"),
                                    Quality = ResponsesMetadata.GetStringPtr(
                                        part.TransformerMetadata, "quality"),
                                    Size = ResponsesMetadata.GetStringPtr(
                                        part.TransformerMetadata, "size"),
                                });
                            }

                            break;

                        case "compaction":
                        case "compaction_summary":
                            if (part.Compact is not null)
                            {
                                AddOutput(ResponsesCompactConvert.CompactionItemFromPart(part, part.Type));
                            }

                            break;
                    }
                }

                if (contentItems.Count > 0)
                {
                    (contentItems, _) = AttachAnnotationsToFirstTextItem(contentItems, message.Annotations);
                    AddOutput(new ResponsesItem
                    {
                        Id = messageItemId,
                        Type = "message",
                        Role = "assistant",
                        Content = new ResponsesInput { Items = contentItems },
                        Status = "completed",
                    });
                }
            }

            // Set status based on finish reason.
            if (choice.FinishReason is { } finishReason)
            {
                switch (finishReason)
                {
                    case "stop":
                        resp.Status = "completed";
                        break;
                    case "length":
                        resp.Status = "incomplete";
                        break;
                    case "tool_calls":
                        resp.Status = "completed";
                        break;
                    case "error":
                        resp.Status = "failed";
                        break;
                }
            }
        }

        // Go appends directly to resp.Output; the local list only becomes
        // visible on the response here.
        resp.Output = output;

        // If no output items were created, create an empty message.
        if (output is not { Count: > 0 })
        {
            resp.Output =
            [
                new ResponsesItem
                {
                    Id = GenerateItemId(),
                    Type = "message",
                    Role = "assistant",
                    Content = new ResponsesInput
                    {
                        Items =
                        [
                            new ResponsesItem
                            {
                                Type = "output_text",
                                Text = string.Empty,
                                Annotations = [],
                            },
                        ],
                    },
                    Status = "completed",
                },
            ];
        }

        return resp;
    }

    /// <summary>Go: <c>generateItemID</c>.</summary>
    public static string GenerateItemId()
        => "item_" + ResponsesConvertUtil.RandomAlphanumeric(16);

    /// <summary>
    /// Go: <c>buildReasoningItems</c>. ReasoningItems preserves the one-to-one
    /// association between a summary and its opaque encrypted content; the
    /// scalar fields are retained as a legacy fallback.
    /// </summary>
    public static List<ResponsesItem> BuildReasoningItems(Message msg)
    {
        var reasoningItems = msg.ReasoningItems;
        if (reasoningItems is not { Count: > 0 })
        {
            reasoningItems =
            [
                new ReasoningItem
                {
                    Content = msg.ReasoningContent ?? string.Empty,
                    Signature = msg.ReasoningSignature ?? string.Empty,
                },
            ];
        }

        var items = new List<ResponsesItem>(reasoningItems.Count);
        foreach (var reasoningItem in reasoningItems)
        {
            var content = reasoningItem.Content ?? string.Empty;
            var signature = reasoningItem.Signature ?? string.Empty;
            if (content.Length == 0 && signature.Length == 0)
            {
                continue;
            }

            var summary = new List<ResponsesReasoningSummary>();
            if (content.Length > 0)
            {
                summary.Add(new ResponsesReasoningSummary
                {
                    Type = "summary_text",
                    Text = content,
                });
            }

            var itemId = reasoningItem.Id;
            if (string.IsNullOrEmpty(itemId))
            {
                itemId = GenerateItemId();
            }

            var item = new ResponsesItem
            {
                Id = itemId,
                Type = "reasoning",
                Status = "completed",
                Summary = summary,
            };
            if (signature.Length > 0)
            {
                item.EncryptedContent = signature;
            }

            items.Add(item);
        }

        return items;
    }
}

/// <summary>Shared normalization helpers for the Responses conversion layer.</summary>
internal static class ResponsesConvertUtil
{
    /// <summary>Go <c>omitempty</c>: a non-null empty string is omitted on the wire.</summary>
    public static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Serializes a value the way Go would store it in <c>map[string]any</c>.</summary>
    public static JsonElement ToElement<T>(T value)
        => JsonSerializer.SerializeToElement(value, LlmJson.Options);

    /// <summary>The literal <c>null</c> as a <see cref="JsonElement"/>.</summary>
    public static JsonElement NullElement()
        => JsonSerializer.SerializeToElement<string?>(null, LlmJson.Options);

    /// <summary>Parses JSON bytes into an element that keeps the exact raw text.</summary>
    public static JsonElement Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>Go <c>utf8.RuneCountInString</c> for valid UTF-8 text.</summary>
    public static long RuneCount(string text)
    {
        long count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private const string AlphanumericCharset =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>Go: <c>lo.RandomString(n, lo.AlphanumericCharset)</c>.</summary>
    public static string RandomAlphanumeric(int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = AlphanumericCharset[
                System.Security.Cryptography.RandomNumberGenerator.GetInt32(AlphanumericCharset.Length)];
        }

        return new string(chars);
    }
}
