using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of llm/transformer/openai/responses/outbound_convert.go: unified
/// model data to Responses API request/response shapes.
/// </summary>
public static class ResponsesOutboundConvert
{
    /// <summary>Go: <c>convertToTextOptions</c>.</summary>
    public static ResponsesTextOptions? ConvertToTextOptions(LlmRequest? chatReq)
    {
        if (chatReq is null)
        {
            return null;
        }

        // Return nil if neither ResponseFormat nor TextVerbosity is set.
        if (chatReq.ResponseFormat is null && chatReq.Verbosity is null)
        {
            return null;
        }

        var result = new ResponsesTextOptions { Verbosity = chatReq.Verbosity };

        if (chatReq.ResponseFormat is { } responseFormat)
        {
            result.Format = new ResponsesTextFormat
            {
                Type = ResponsesConvertUtil.NullIfEmpty(responseFormat.Type),
            };

            // Extract name, schema, strict, and description from json_schema.
            if (responseFormat.Type == "json_schema"
                && responseFormat.JsonSchema is { } rawSchema
                && rawSchema.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                ResponsesRawJsonSchema? jsonSchema = null;
                try
                {
                    jsonSchema = rawSchema.Deserialize<ResponsesRawJsonSchema>(LlmJson.Options);
                }
                catch (JsonException)
                {
                    // Go ignores the unmarshal error and keeps type-only format.
                }

                if (jsonSchema is not null)
                {
                    result.Format.Name = ResponsesConvertUtil.NullIfEmpty(jsonSchema.Name);
                    result.Format.Description = ResponsesConvertUtil.NullIfEmpty(jsonSchema.Description);
                    result.Format.Schema = jsonSchema.Schema is { } schema
                        ? GoJson.CompactElement(schema)
                        : null;
                    result.Format.Strict = jsonSchema.Strict;
                }
            }
        }

        return result;
    }

    /// <summary>Go: <c>convertInstructionsFromMessages</c>.</summary>
    public static string ConvertInstructionsFromMessages(List<Message> msgs)
    {
        if (msgs.Count == 0)
        {
            return string.Empty;
        }

        var instructions = new List<string>();

        // Collect text from the system messages.
        foreach (var msg in msgs)
        {
            if (msg.Role != "system")
            {
                continue;
            }

            if (msg.Content?.Content is { } content)
            {
                instructions.Add(content);
            }

            if (msg.Content?.MultipleContent is { Count: > 0 } parts)
            {
                var builder = new StringBuilder();

                foreach (var part in parts)
                {
                    if (part.Type == "text" && part.Text is { } text)
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append('\n');
                        }

                        builder.Append(text);
                    }
                }

                if (builder.Length > 0)
                {
                    instructions.Add(builder.ToString());
                }
            }
        }

        return string.Join("\n", instructions);
    }

    /// <summary>
    /// Go: <c>convertInputFromMessages</c>. User messages become items with a
    /// content array of input_text items; assistant messages become message
    /// items with output_text content; tool calls become function_call items
    /// and tool results become function_call_output items.
    /// </summary>
    public static ResponsesInput ConvertInputFromMessages(List<Message> msgs, TransformOptions? transformOptions)
    {
        if (msgs.Count == 0)
        {
            return new ResponsesInput();
        }

        var wasArrayFormat = transformOptions?.ArrayInputs == true;

        if (msgs.Count == 1 && msgs[0].Content?.Content is { } singleContent && !wasArrayFormat)
        {
            return new ResponsesInput { Text = singleContent };
        }

        List<ResponsesItem>? items = null;

        void AddItem(ResponsesItem item)
        {
            items ??= [];
            items.Add(item);
        }

        // Track tool call types so tool result messages can be encoded correctly.
        var toolResultItemTypeByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var msg in msgs)
        {
            switch (msg.Role)
            {
                case "user":
                case "developer":
                    AddItem(ConvertUserMessage(msg));
                    break;

                case "assistant":
                    {
                        var assistantItems = ConvertAssistantMessage(msg);
                        foreach (var item in assistantItems)
                        {
                            AddItem(item);
                        }

                        // Record tool call types for later tool result encoding.
                        foreach (var item in assistantItems)
                        {
                            switch (item.Type)
                            {
                                case "function_call":
                                    if (!string.IsNullOrEmpty(item.CallId))
                                    {
                                        toolResultItemTypeByCallId[item.CallId] = "function_call_output";
                                    }

                                    break;
                                case "custom_tool_call":
                                    if (!string.IsNullOrEmpty(item.CallId))
                                    {
                                        toolResultItemTypeByCallId[item.CallId] = "custom_tool_call_output";
                                    }

                                    break;
                            }
                        }

                        break;
                    }

                case "tool":
                    {
                        var itemType = "function_call_output";

                        if (msg.ToolCallId is { } toolCallId
                            && toolResultItemTypeByCallId.TryGetValue(toolCallId, out var mapped))
                        {
                            itemType = mapped;
                        }

                        AddItem(ConvertToolMessageWithType(msg, itemType));
                        break;
                    }
            }
        }

        return new ResponsesInput { Items = items };
    }

    /// <summary>Go: <c>convertUserMessage</c>.</summary>
    public static ResponsesItem ConvertUserMessage(Message msg)
    {
        List<ResponsesItem>? contentItems = null;

        void AddItem(ResponsesItem item)
        {
            contentItems ??= [];
            contentItems.Add(item);
        }

        if (msg.Content?.Content is { } content)
        {
            AddItem(new ResponsesItem
            {
                Type = "input_text",
                Text = content,
            });
        }
        else if (msg.Content?.MultipleContent is { } parts)
        {
            foreach (var part in parts)
            {
                switch (part.Type)
                {
                    case "text":
                        if (part.Text is { } text)
                        {
                            AddItem(new ResponsesItem
                            {
                                Type = "input_text",
                                Text = text,
                            });
                        }

                        break;

                    case "image_url":
                        if (part.ImageUrl is { } imageUrl)
                        {
                            AddItem(new ResponsesItem
                            {
                                Type = "input_image",
                                ImageUrl = imageUrl.Url,
                                Detail = imageUrl.Detail,
                            });
                        }

                        break;

                    case "document":
                        if (part.Document is { } document)
                        {
                            AddItem(ResponseInputFile(document));
                        }

                        break;

                    case "compaction":
                    case "compaction_summary":
                        if (part.Compact is not null)
                        {
                            AddItem(ResponsesCompactConvert.CompactionItemFromPart(part, part.Type));
                        }

                        break;
                }
            }
        }

        return new ResponsesItem
        {
            Type = "message",
            Role = msg.Role ?? string.Empty,
            Content = new ResponsesInput { Items = contentItems },
        };
    }

    /// <summary>Go: <c>responseInputFile</c>.</summary>
    public static ResponsesItem ResponseInputFile(DocumentUrl document)
    {
        var item = new ResponsesItem { Type = "input_file" };

        if (!string.IsNullOrEmpty(document.FileId))
        {
            item.FileId = document.FileId;
        }

        if (!string.IsNullOrEmpty(document.Filename))
        {
            item.Filename = document.Filename;
        }
        else if (document.MimeType == "application/pdf")
        {
            item.Filename = "document.pdf";
        }

        if (!string.IsNullOrEmpty(document.Url))
        {
            if (document.Url.StartsWith("data:", StringComparison.Ordinal))
            {
                item.FileData = document.Url;
            }
            else
            {
                item.FileUrl = document.Url;
            }
        }

        return item;
    }

    /// <summary>
    /// Go: <c>convertAssistantMessage</c>. Returns multiple items when the
    /// message contains tool calls.
    /// </summary>
    public static List<ResponsesItem> ConvertAssistantMessage(Message msg)
    {
        List<ResponsesItem>? items = null;
        List<ResponsesItem>? toolCallItems = null;
        List<ResponsesItem>? contentItems = null;

        void AddItem(ResponsesItem item)
        {
            items ??= [];
            items.Add(item);
        }

        // Handle reasoning content first. For Requests, reasoning is represented
        // as an input item with type "reasoning"; the summary field holds the
        // reasoning summary text.
        var reasoningItems = msg.ReasoningItems;
        if (reasoningItems is not { Count: > 0 } && msg.ReasoningSignature is not null)
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

        if (reasoningItems is { } itemsToEncode)
        {
            foreach (var reasoningItem in itemsToEncode)
            {
                var encryptedContent = OpenAiSignatures.DecodeOpenAIEncryptedContent(reasoningItem.Signature);
                if (encryptedContent is null)
                {
                    continue;
                }

                var summary = new List<ResponsesReasoningSummary>();
                if (!string.IsNullOrEmpty(reasoningItem.Content))
                {
                    summary.Add(new ResponsesReasoningSummary
                    {
                        Type = "summary_text",
                        Text = reasoningItem.Content,
                    });
                }

                AddItem(new ResponsesItem
                {
                    Id = reasoningItem.Id ?? string.Empty,
                    Type = "reasoning",
                    EncryptedContent = encryptedContent,
                    Summary = summary,
                });
            }
        }

        // Handle tool calls.
        if (msg.ToolCalls is { } toolCalls)
        {
            foreach (var toolCall in toolCalls)
            {
                if (toolCall.ResponseCustomToolCall is { } custom)
                {
                    toolCallItems ??= [];
                    toolCallItems.Add(new ResponsesItem
                    {
                        Type = "custom_tool_call",
                        CallId = custom.CallId,
                        Name = custom.Name,
                        Input = custom.Input,
                    });
                }
                else
                {
                    toolCallItems ??= [];
                    toolCallItems.Add(new ResponsesItem
                    {
                        Type = "function_call",
                        CallId = toolCall.Id ?? string.Empty,
                        Name = ResponsesNamespace.LocalFunctionName(
                            toolCall.Function.Namespace,
                            toolCall.Function.Name),
                        Namespace = toolCall.Function.Namespace ?? string.Empty,
                        Arguments = toolCall.Function.Arguments,
                    });
                }
            }
        }

        void FlushMessage()
        {
            if (contentItems is not { Count: > 0 })
            {
                return;
            }

            AddItem(new ResponsesItem
            {
                Type = "message",
                Role = msg.Role ?? string.Empty,
                Status = "completed",
                Content = new ResponsesInput { Items = contentItems },
            });
            contentItems = null;
        }

        if (msg.Content?.Content is { } content)
        {
            contentItems ??= [];
            contentItems.Add(new ResponsesItem
            {
                Type = "output_text",
                Text = content,
            });
        }
        else if (msg.Content?.MultipleContent is { } parts)
        {
            foreach (var part in parts)
            {
                switch (part.Type)
                {
                    case "text":
                        if (part.Text is { } text)
                        {
                            contentItems ??= [];
                            contentItems.Add(new ResponsesItem
                            {
                                Type = "output_text",
                                Text = text,
                            });
                        }

                        break;

                    case "compaction":
                    case "compaction_summary":
                        if (part.Compact is not null)
                        {
                            FlushMessage();
                            AddItem(ResponsesCompactConvert.CompactionItemFromPart(part, part.Type));
                        }

                        break;
                }
            }
        }

        // In the common assistant flow, the visible message content precedes any
        // subsequent tool calls. Flush message segments before appending tool-call
        // items so the encoded Responses item order matches that expectation.
        FlushMessage();

        if (toolCallItems is { Count: > 0 })
        {
            items ??= [];
            items.AddRange(toolCallItems);
        }

        return items ?? [];
    }

    /// <summary>Go: <c>convertToolMessageWithType</c>.</summary>
    public static ResponsesItem ConvertToolMessageWithType(Message msg, string itemType)
    {
        var output = new ResponsesInput();

        // Handle simple content first.
        if (msg.Content?.Content is { } content)
        {
            output.Text = content;
        }
        else if (msg.Content?.MultipleContent is { Count: > 0 } parts)
        {
            foreach (var part in parts)
            {
                switch (part.Type)
                {
                    case "text":
                        if (part.Text is { } text)
                        {
                            output.Items ??= [];
                            output.Items.Add(new ResponsesItem
                            {
                                Type = "input_text",
                                Text = text,
                            });
                        }

                        break;

                    case "image_url":
                        // Tool results can carry images; detail is required by the
                        // InputImageContent schema, so always send one.
                        if (part.ImageUrl is { } imageUrl)
                        {
                            output.Items ??= [];
                            output.Items.Add(new ResponsesItem
                            {
                                Type = "input_image",
                                ImageUrl = imageUrl.Url,
                                Detail = imageUrl.Detail ?? "auto",
                            });
                        }

                        break;
                }
            }
        }

        // Sometimes the tool result is empty, so add an empty string.
        if (output.Text is null && output.Items is not { Count: > 0 })
        {
            output.Text = string.Empty;
        }

        return new ResponsesItem
        {
            Type = itemType,
            CallId = msg.ToolCallId ?? string.Empty,
            Output = output,
        };
    }

    /// <summary>Go: <c>convertImageGenerationToTool</c>.</summary>
    public static ResponsesTool ConvertImageGenerationToTool(Tool src)
    {
        var tool = new ResponsesTool { Type = "image_generation" };
        if (src.ImageGeneration is { } imageGeneration)
        {
            tool.Model = ResponsesConvertUtil.NullIfEmpty(imageGeneration.Model);
            tool.Background = ResponsesConvertUtil.NullIfEmpty(imageGeneration.Background);
            tool.InputFidelity = ResponsesConvertUtil.NullIfEmpty(imageGeneration.InputFidelity);
            tool.InputImageMask = imageGeneration.InputImageMask;
            tool.Moderation = ResponsesConvertUtil.NullIfEmpty(imageGeneration.Moderation);
            tool.OutputCompression = imageGeneration.OutputCompression;
            tool.OutputFormat = ResponsesConvertUtil.NullIfEmpty(imageGeneration.OutputFormat);
            tool.PartialImages = imageGeneration.PartialImages;
            tool.Quality = ResponsesConvertUtil.NullIfEmpty(imageGeneration.Quality);
            tool.Size = ResponsesConvertUtil.NullIfEmpty(imageGeneration.Size);
        }

        return tool;
    }

    /// <summary>Go: <c>convertWebSearchToTool</c>.</summary>
    public static ResponsesTool ConvertWebSearchToTool(Tool src)
    {
        var tool = new ResponsesTool { Type = "web_search" };

        if (src.WebSearch is not { } webSearch)
        {
            return tool;
        }

        if (webSearch.AllowedDomains is { Count: > 0 } allowedDomains)
        {
            tool.Filters = new ResponsesWebSearchFilters { AllowedDomains = [.. allowedDomains] };
        }

        var location = webSearch.UserLocation;
        if (location is not null
            && (!string.IsNullOrEmpty(location.Type)
                || !string.IsNullOrEmpty(location.City)
                || !string.IsNullOrEmpty(location.Country)
                || !string.IsNullOrEmpty(location.Region)
                || !string.IsNullOrEmpty(location.Timezone)))
        {
            var locationType = location.Type;
            if (string.IsNullOrEmpty(locationType))
            {
                locationType = "approximate";
            }

            tool.UserLocation = new ResponsesWebSearchUserLocation
            {
                Type = locationType,
                City = ResponsesConvertUtil.NullIfEmpty(location.City),
                Country = ResponsesConvertUtil.NullIfEmpty(location.Country),
                Region = ResponsesConvertUtil.NullIfEmpty(location.Region),
                Timezone = ResponsesConvertUtil.NullIfEmpty(location.Timezone),
            };
        }

        return tool;
    }

    /// <summary>Go: <c>convertCustomToTool</c>.</summary>
    public static ResponsesTool ConvertCustomToTool(Tool src)
    {
        var tool = new ResponsesTool { Type = "custom" };
        if (src.ResponseCustomTool is { } customTool)
        {
            tool.Name = customTool.Name;
            tool.Description = ResponsesConvertUtil.NullIfEmpty(customTool.Description);
            if (customTool.Format is { } format)
            {
                tool.Format = new ResponsesCustomToolFormat
                {
                    Type = format.Type,
                    Syntax = ResponsesConvertUtil.NullIfEmpty(format.Syntax),
                    Definition = ResponsesConvertUtil.NullIfEmpty(format.Definition),
                };
            }
        }

        return tool;
    }

    /// <summary>Go: <c>convertFunctionToTool</c>.</summary>
    public static ResponsesTool ConvertFunctionToTool(Tool src)
    {
        var tool = new ResponsesTool
        {
            Type = "function",
            Name = ResponsesNamespace.LocalFunctionName(src.Function.Namespace, src.Function.Name),
            Description = ResponsesConvertUtil.NullIfEmpty(src.Function.Description),
            Strict = src.Function.Strict,
        };

        // Convert parameters from the unified JSON representation to a map.
        if (src.Function.Parameters is { } rawParameters
            && rawParameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            Dictionary<string, JsonElement>? parameters = null;
            try
            {
                parameters = rawParameters.Deserialize<Dictionary<string, JsonElement>>(LlmJson.Options);
            }
            catch (JsonException)
            {
                // Go leaves tool.Parameters nil when the raw parameters do not decode.
            }

            if (parameters is not null)
            {
                // OpenAI rejects object schemas that omit properties entirely.
                // Anthropic clients may send {"type":"object"} for no-arg tools,
                // so normalize that here.
                if (parameters.TryGetValue("type", out var typeName)
                    && typeName.ValueKind == JsonValueKind.String
                    && typeName.GetString() == "object"
                    && (!parameters.TryGetValue("properties", out var properties)
                        || properties.ValueKind != JsonValueKind.Object))
                {
                    parameters["properties"] = ResponsesConvertUtil.ToElement(
                        new Dictionary<string, JsonElement>(StringComparer.Ordinal));
                }

                // For strict mode, additionalProperties must be false and all
                // properties must be listed in required.
                if (src.Function.Strict is true)
                {
                    parameters["additionalProperties"] = ResponsesConvertUtil.ToElement(false);

                    if (parameters.TryGetValue("properties", out var props)
                        && props.ValueKind == JsonValueKind.Object
                        && props.GetPropertyCount() > 0)
                    {
                        var required = new List<string>();

                        // First, preserve any existing required array.
                        if (parameters.TryGetValue("required", out var existingRequired)
                            && existingRequired.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in existingRequired.EnumerateArray())
                            {
                                if (entry.ValueKind == JsonValueKind.String)
                                {
                                    required.Add(entry.GetString()!);
                                }
                            }
                        }

                        // Add any missing property keys to required.
                        var requiredSet = new HashSet<string>(required, StringComparer.Ordinal);
                        foreach (var property in props.EnumerateObject())
                        {
                            if (!requiredSet.Contains(property.Name))
                            {
                                required.Add(property.Name);
                            }
                        }

                        parameters["required"] = ResponsesConvertUtil.ToElement(required);
                    }
                }

                // Go: map[string]any with omitempty — an empty map is omitted.
                tool.Parameters = parameters.Count > 0 ? parameters : null;
            }
        }

        return tool;
    }

    /// <summary>Go: <c>convertToolChoice</c>.</summary>
    public static ResponsesToolChoice? ConvertToolChoice(ToolChoice? src)
    {
        if (src is null)
        {
            return null;
        }

        var result = new ResponsesToolChoice();

        if (src.ToolChoiceMode is not null)
        {
            // String mode like "none", "auto", "required".
            result.Mode = src.ToolChoiceMode;
        }

        if (src.NamedToolChoice is { } namedToolChoice)
        {
            // Specific tool choice.
            result.Type = namedToolChoice.Type;
            if (!string.IsNullOrEmpty(namedToolChoice.Function.Name))
            {
                result.Name = namedToolChoice.Function.Name;
            }
        }

        if (src.Tools is { } tools)
        {
            foreach (var option in tools)
            {
                result.Tools ??= [];
                result.Tools.Add(new ResponsesToolOption { Type = option.Type, Name = option.Name });
            }
        }

        return result;
    }

    /// <summary>
    /// Go: <c>convertStreamOptions</c>. IncludeObfuscation is read from
    /// TransformerMetadata since it is a Responses API specific field.
    /// </summary>
    public static ResponsesStreamOptions? ConvertStreamOptions(
        StreamOptions? src, Dictionary<string, JsonElement>? metadata)
    {
        if (src is null)
        {
            return null;
        }

        var includeObfuscation = ResponsesMetadata.GetBoolPtr(metadata, "include_obfuscation");
        if (includeObfuscation is null)
        {
            return null;
        }

        return new ResponsesStreamOptions { IncludeObfuscation = includeObfuscation };
    }

    /// <summary>
    /// Go: <c>convertReasoning</c>. Only one of reasoning.effort and
    /// reasoning.max_tokens can be specified; effort wins when both are present.
    /// </summary>
    public static ResponsesReasoning? ConvertReasoning(LlmRequest req)
    {
        var reasoningContext = ResponsesRequestExtensions.OpenAiResponsesRequestExtensions(req)
            ?.ReasoningContext ?? string.Empty;

        // Check if any reasoning-related fields are present.
        var hasReasoningFields = reasoningContext.Length > 0
            || !string.IsNullOrEmpty(req.ReasoningEffort)
            || req.ReasoningBudget is not null
            || req.ReasoningSummary is not null;
        if (!hasReasoningFields)
        {
            return null;
        }

        var reasoning = new ResponsesReasoning
        {
            Context = ResponsesConvertUtil.NullIfEmpty(reasoningContext),
            Effort = ResponsesConvertUtil.NullIfEmpty(req.ReasoningEffort),
            MaxTokens = req.ReasoningBudget,
        };

        // Handle summary field (generate_summary is already merged at inbound).
        if (req.ReasoningSummary is not null)
        {
            reasoning.Summary = ResponsesConvertUtil.NullIfEmpty(req.ReasoningSummary);
        }

        // If both effort and budget are specified, prioritize effort.
        if (!string.IsNullOrEmpty(req.ReasoningEffort) && req.ReasoningBudget is not null)
        {
            reasoning.MaxTokens = null;
        }

        return reasoning;
    }

    /// <summary>Go: <c>annotationToLLM</c>.</summary>
    public static Annotation AnnotationToLlm(ResponsesAnnotation annotation, long textRuneOffset)
    {
        var result = new Annotation
        {
            Type = ResponsesConvertUtil.NullIfEmpty(annotation.Type),
        };

        if (annotation.StartIndex is { } startIndex)
        {
            result.StartIndex = startIndex + textRuneOffset;
        }

        if (annotation.EndIndex is { } endIndex)
        {
            result.EndIndex = endIndex + textRuneOffset;
        }

        if (annotation.UrlCitation is { } citation)
        {
            result.UrlCitation = new UrlCitation
            {
                Url = ResponsesConvertUtil.NullIfEmpty(citation.Url),
                Title = ResponsesConvertUtil.NullIfEmpty(citation.Title),
            };
        }

        return result;
    }

    /// <summary>Go: <c>appendOutputText</c>.</summary>
    public static List<Annotation> AppendOutputText(
        StringBuilder textContent,
        ref long visibleTextRuneCount,
        List<Annotation> annotations,
        ResponsesItem outputItem)
    {
        if (outputItem.Text is not { } text)
        {
            return annotations;
        }

        var textRuneOffset = visibleTextRuneCount;
        textContent.Append(text);
        visibleTextRuneCount += ResponsesConvertUtil.RuneCount(text);

        if (outputItem.Annotations is not { Count: > 0 } outputAnnotations)
        {
            return annotations;
        }

        foreach (var annotation in outputAnnotations)
        {
            annotations.Add(AnnotationToLlm(annotation, textRuneOffset));
        }

        return annotations;
    }

    /// <summary>Go: <c>appendResponseWebSearchCallMetadata</c>.</summary>
    public static void AppendResponseWebSearchCallMetadata(
        Dictionary<string, JsonElement>? transformerMetadata, ResponsesItem outputItem)
    {
        if (transformerMetadata is null
            || outputItem.Id.Length == 0
            || outputItem.Action?.WebSearch is not { } src)
        {
            return;
        }

        var action = new ResponsesWebSearchAction
        {
            Type = src.Type,
            Query = src.Query,
        };
        if (src.Queries is { Count: > 0 } queries)
        {
            action.Queries = [.. queries];
        }

        if (src.Sources is { Count: > 0 } sources)
        {
            // Go decodes a null element as the zero WebSearchSource struct.
            action.Sources = [.. sources.Select(static source => source ?? new ResponsesWebSearchSource())];
        }

        var call = new ResponsesItem
        {
            Id = outputItem.Id,
            Type = outputItem.Type,
            Status = outputItem.Status,
            Action = ResponsesItemAction.NewWebSearchAction(action),
        };

        // Go stores an in-process []Item and replaces the key when the value is
        // not that type; the port stores the JSON form, so decode, update, and
        // re-encode. Snapshots are immutable and merged by call ID: added, done
        // and the terminal response can all describe the same invocation.
        List<ResponsesItem>? previous = null;
        if (transformerMetadata.TryGetValue(ResponsesKeys.WebSearchCallsTransformerMetadataKey, out var raw)
            && raw.ValueKind == JsonValueKind.Array)
        {
            try
            {
                previous = raw.Deserialize<List<ResponsesItem>>(LlmJson.Options);
            }
            catch (JsonException)
            {
                previous = null;
            }
        }

        List<ResponsesItem> calls = previous is null ? [] : [.. previous];
        for (var i = 0; i < calls.Count; i++)
        {
            var existing = calls[i];
            if (existing.Id != call.Id)
            {
                continue;
            }

            if (call.Action?.WebSearch is null)
            {
                call.Action = existing.Action;
            }
            else if (existing.Action?.WebSearch is { } earlier)
            {
                var merged = call.Action.WebSearch;
                if (string.IsNullOrEmpty(merged.Type))
                {
                    merged.Type = earlier.Type;
                }

                if (string.IsNullOrEmpty(merged.Query))
                {
                    merged.Query = earlier.Query;
                }

                if (merged.Queries is not { Count: > 0 })
                {
                    merged.Queries = earlier.Queries is { Count: > 0 } earlierQueries
                        ? [.. earlierQueries]
                        : null;
                }

                var mergedSources = earlier.Sources is { Count: > 0 }
                    ? earlier.Sources
                        .Select(static source => source ?? new ResponsesWebSearchSource())
                        .ToList()
                    : [];
                foreach (var source in merged.Sources ?? [])
                {
                    var index = mergedSources.FindIndex(existingSource =>
                        existingSource.Url == source.Url);
                    if (index < 0)
                    {
                        mergedSources.Add(source);
                    }
                    else if (!string.IsNullOrEmpty(source.Title))
                    {
                        mergedSources[index] = source;
                    }
                }

                merged.Sources = mergedSources.Count > 0 ? mergedSources : null;
            }

            if (call.Status is null
                || existing.Status is "completed" or "failed")
            {
                call.Status = existing.Status;
            }

            calls[i] = call;
            transformerMetadata[ResponsesKeys.WebSearchCallsTransformerMetadataKey] =
                ResponsesConvertUtil.ToElement(calls);
            return;
        }

        calls.Add(call);
        transformerMetadata[ResponsesKeys.WebSearchCallsTransformerMetadataKey] =
            ResponsesConvertUtil.ToElement(calls);
    }

    /// <summary>
    /// Go: <c>convertOutputToMessage</c>. Aggregates text, reasoning, tool
    /// calls, image generation, compaction and compaction_summary items from the
    /// response output.
    /// </summary>
    public static Message ConvertOutputToMessage(
        List<ResponsesItem> output, Dictionary<string, JsonElement>? transformerMetadata)
    {
        List<MessageContentPart>? contentParts = null;
        var textContent = new StringBuilder();
        var reasoningContent = new StringBuilder();
        string? reasoningSignature = null;
        List<ReasoningItem>? reasoningItems = null;
        var messageId = string.Empty;
        List<ToolCall>? toolCalls = null;
        List<Annotation> annotations = [];
        long visibleTextRuneCount = 0;

        void FlushText()
        {
            if (textContent.Length == 0)
            {
                return;
            }

            contentParts ??= [];
            contentParts.Add(new MessageContentPart
            {
                Type = "text",
                Text = textContent.ToString(),
            });
            textContent.Clear();
        }

        foreach (var rawOutputItem in output)
        {
            // Go decodes a null element as the zero Item struct (type "").
            var outputItem = rawOutputItem ?? new ResponsesItem();
            switch (outputItem.Type)
            {
                case "message":
                    if (messageId.Length == 0)
                    {
                        messageId = outputItem.Id;
                    }

                    if (outputItem.Content is not { } content)
                    {
                        continue;
                    }

                    if (content.Items is { } contentItems)
                    {
                        foreach (var contentItem in contentItems)
                        {
                            if (contentItem.Type == "output_text")
                            {
                                annotations = AppendOutputText(
                                    textContent, ref visibleTextRuneCount, annotations, contentItem);
                            }
                        }
                    }

                    break;

                case "output_text":
                    annotations = AppendOutputText(
                        textContent, ref visibleTextRuneCount, annotations, outputItem);
                    break;

                case "function_call":
                    toolCalls ??= [];
                    toolCalls.Add(new ToolCall
                    {
                        Id = ResponsesConvertUtil.NullIfEmpty(outputItem.CallId),
                        Type = ToolTypes.Function,
                        Function = new FunctionCall
                        {
                            Name = ResponsesNamespace.FlatFunctionName(
                                outputItem.Namespace, outputItem.Name),
                            Namespace = ResponsesConvertUtil.NullIfEmpty(outputItem.Namespace),
                            Arguments = outputItem.Arguments,
                        },
                    });
                    break;

                case "custom_tool_call":
                    toolCalls ??= [];
                    toolCalls.Add(new ToolCall
                    {
                        Id = ResponsesConvertUtil.NullIfEmpty(outputItem.CallId),
                        Type = ToolTypes.ResponsesCustomTool,
                        ResponseCustomToolCall = new ResponseCustomToolCall
                        {
                            CallId = outputItem.CallId,
                            Name = outputItem.Name,
                            Input = outputItem.Input ?? string.Empty,
                        },
                    });
                    break;

                case "reasoning":
                    {
                        var itemReasoning = new StringBuilder();
                        if (outputItem.Summary is { } summary)
                        {
                            foreach (var entry in summary)
                            {
                                reasoningContent.Append(entry.Text);
                                itemReasoning.Append(entry.Text);
                            }
                        }

                        var itemSignature = string.Empty;
                        if (!string.IsNullOrEmpty(outputItem.EncryptedContent))
                        {
                            reasoningSignature = OpenAiSignatures.EncodeOpenAIEncryptedContent(
                                outputItem.EncryptedContent);
                            itemSignature = reasoningSignature ?? string.Empty;
                        }

                        if (itemReasoning.Length > 0 || itemSignature.Length > 0)
                        {
                            reasoningItems ??= [];
                            reasoningItems.Add(new ReasoningItem
                            {
                                Id = ResponsesConvertUtil.NullIfEmpty(outputItem.Id),
                                Content = ResponsesConvertUtil.NullIfEmpty(itemReasoning.ToString()),
                                Signature = ResponsesConvertUtil.NullIfEmpty(itemSignature),
                            });
                        }

                        break;
                    }

                case "image_generation_call":
                    {
                        FlushText();

                        var imageOutputFormat = "png";

                        if (ResponsesMetadata.GetStringPtr(transformerMetadata, "image_output_format")
                            is { Length: > 0 } configuredFormat)
                        {
                            imageOutputFormat = configuredFormat;
                        }

                        if (!string.IsNullOrEmpty(outputItem.Result))
                        {
                            contentParts ??= [];
                            contentParts.Add(new MessageContentPart
                            {
                                Type = "image_url",
                                ImageUrl = new ImageUrl
                                {
                                    Url = "data:image/" + imageOutputFormat + ";base64," + outputItem.Result,
                                },
                                TransformerMetadata = new Dictionary<string, JsonElement>(
                                    StringComparer.Ordinal)
                                {
                                    ["background"] = ResponsesConvertUtil.ToElement(outputItem.Background),
                                    ["output_format"] = ResponsesConvertUtil.ToElement(outputItem.OutputFormat),
                                    ["quality"] = ResponsesConvertUtil.ToElement(outputItem.Quality),
                                    ["size"] = ResponsesConvertUtil.ToElement(outputItem.Size),
                                },
                            });
                        }

                        break;
                    }

                case "web_search_call":
                    AppendResponseWebSearchCallMetadata(transformerMetadata, outputItem);
                    break;

                case "compaction":
                case "compaction_summary":
                    {
                        FlushText();

                        contentParts ??= [];
                        contentParts.Add(new MessageContentPart
                        {
                            Type = outputItem.Type,
                            Compact = new CompactContent
                            {
                                Id = ResponsesConvertUtil.NullIfEmpty(outputItem.Id),
                                EncryptedContent = ResponsesConvertUtil.NullIfEmpty(
                                    outputItem.EncryptedContent),
                                CreatedBy = outputItem.CreatedBy,
                            },
                        });
                        break;
                    }

                case "input_image":
                    {
                        FlushText();

                        if (!string.IsNullOrEmpty(outputItem.ImageUrl))
                        {
                            contentParts ??= [];
                            contentParts.Add(new MessageContentPart
                            {
                                Type = "image_url",
                                ImageUrl = new ImageUrl { Url = outputItem.ImageUrl },
                            });
                        }

                        break;
                    }
            }
        }

        FlushText();

        var msg = new Message
        {
            Id = ResponsesConvertUtil.NullIfEmpty(messageId),
            Role = "assistant",
            ToolCalls = toolCalls is { Count: > 0 } ? toolCalls : null,
            Annotations = annotations.Count > 0 ? annotations : null,
        };

        if (reasoningContent.Length > 0)
        {
            msg.ReasoningContent = reasoningContent.ToString();
        }

        if (reasoningSignature is not null)
        {
            msg.ReasoningSignature = reasoningSignature;
        }

        if (reasoningItems is { Count: > 0 })
        {
            msg.ReasoningItems = reasoningItems;
        }

        if (contentParts is { Count: 1 }
            && contentParts[0].Type == "text"
            && (toolCalls is not { Count: > 0 }))
        {
            msg.Content = new MessageContent { Content = contentParts[0].Text };
        }
        else if (contentParts is { Count: > 0 })
        {
            msg.Content = new MessageContent { MultipleContent = contentParts };
        }

        return msg;
    }
}
