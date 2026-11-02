using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;
using GeminiFunctionCall = AxonHub.Llm.Transformer.Gemini.FunctionCall;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>
/// Port of llm/transformer/gemini/inbound_convert.go: the native Gemini
/// inbound conversions between GenerateContentRequest/Response and the
/// unified request/response models.
/// </summary>
public static class GeminiInboundConvert
{
    /// <summary>Port of convertGeminiToLLMRequest (inbound_convert.go:20-247).</summary>
    public static LlmRequest ConvertGeminiToLlmRequest(GenerateContentRequest geminiReq)
    {
        var chatReq = new LlmRequest
        {
            RequestType = RequestTypes.Chat,
            ApiFormat = ApiFormats.GeminiContents,
        };

        // Convert generation config.
        if (geminiReq.GenerationConfig is { } gc)
        {
            if (gc.MaxOutputTokens > 0)
            {
                chatReq.MaxTokens = gc.MaxOutputTokens;
            }

            if (gc.Temperature is { } temperature)
            {
                chatReq.Temperature = temperature;
            }

            if (gc.TopP is { } topP)
            {
                chatReq.TopP = topP;
            }

            if (gc.PresencePenalty is { } presencePenalty)
            {
                chatReq.PresencePenalty = presencePenalty;
            }

            if (gc.FrequencyPenalty is { } frequencyPenalty)
            {
                chatReq.FrequencyPenalty = frequencyPenalty;
            }

            if (gc.Seed is { } seed)
            {
                chatReq.Seed = seed;
            }

            if (gc.StopSequences is { Count: > 0 })
            {
                chatReq.Stop = gc.StopSequences.Count == 1
                    ? new Stop { Single = gc.StopSequences[0] }
                    : new Stop { Multiple = gc.StopSequences };
            }

            // Convert thinking config to reasoning effort and preserve budget.
            // Priority 1: use ThinkingLevel if provided.
            // Priority 2: convert from ThinkingBudget if provided.
            if (gc.ThinkingConfig is { } thinkingConfig)
            {
                chatReq.ExtraBody = ConvertGeminiThinkingConfigToGeminiOpenAIExtraBody(thinkingConfig);

                if (thinkingConfig.ThinkingLevel is { Length: > 0 })
                {
                    // ThinkingLevel has priority - use it directly.
                    chatReq.ReasoningEffort = thinkingConfig.ThinkingLevel.ToLowerInvariant();
                    // Gemini "minimal" maps to LLM "low" for consistency.
                    if (chatReq.ReasoningEffort == "minimal")
                    {
                        chatReq.ReasoningEffort = "low";
                    }
                }
                else if (thinkingConfig.ThinkingBudget is { } budget)
                {
                    // No ThinkingLevel, convert from ThinkingBudget.
                    chatReq.ReasoningEffort = GeminiConvert.ThinkingBudgetToReasoningEffort(budget);
                }
                else
                {
                    // No level or budget, use default.
                    chatReq.ReasoningEffort = "medium";
                }

                // Always preserve the original budget if present.
                if (thinkingConfig.ThinkingBudget is { } originalBudget)
                {
                    chatReq.ReasoningBudget = originalBudget;
                }
            }

            // Convert responseModalities to modalities.
            if (gc.ResponseModalities is { Count: > 0 })
            {
                chatReq.Modalities = GeminiConvert.ConvertGeminiModalitiesToLlm(gc.ResponseModalities);
            }

            // Convert ResponseSchema/ResponseJsonSchema to ResponseFormat json_schema.
            if (gc.ResponseJsonSchema is { } responseJsonSchema)
            {
                chatReq.ResponseFormat = GeminiJsonSchemaResponseFormat(responseJsonSchema);
            }
            else if (gc.ResponseSchema is { } responseSchema)
            {
                chatReq.ResponseFormat = GeminiJsonSchemaResponseFormat(responseSchema);
            }
            else if (gc.ResponseMIMEType == "application/json")
            {
                // If only ResponseMIMEType is set to JSON, use json_object.
                chatReq.ResponseFormat = new ResponseFormat { Type = "json_object" };
            }
        }

        // Convert system instruction.
        var messages = new List<Message>();

        if (geminiReq.SystemInstruction is { } systemInstruction)
        {
            var systemText = GeminiConvert.ExtractTextFromContent(systemInstruction);
            if (systemText is { Length: > 0 })
            {
                messages.Add(new Message
                {
                    Role = "system",
                    Content = new MessageContent { Content = systemText },
                });
            }
        }

        // Convert contents to messages.
        if (geminiReq.Contents is { } geminiContents)
        {
            for (var i = 0; i < geminiContents.Count; i++)
            {
                var converted = ConvertGeminiContentToLLMMessages(
                    geminiContents[i], geminiContents.Take(i).ToList());
                if (converted is not null)
                {
                    messages.AddRange(converted);
                }
            }
        }

        chatReq.Messages = messages;

        // Convert tools.
        if (geminiReq.Tools is { Count: > 0 })
        {
            var tools = new List<AxonHub.Llm.Tool>();

            foreach (var tool in geminiReq.Tools)
            {
                // Handle function declarations.
                if (tool.FunctionDeclarations is { } declarations)
                {
                    foreach (var fd in declarations)
                    {
                        // The gemini sdk uses UPPER case for type, but the
                        // unified format uses lower case. A failed transform
                        // falls back to the original schema, like the
                        // reference.
                        static JsonElement? Transform(JsonElement? schema)
                        {
                            if (schema is not { } raw)
                            {
                                return null;
                            }

                            try
                            {
                                return XjsonSchema.Transform(raw, sub =>
                                {
                                    if (sub.TryGetPropertyValue("type", out var typeValue)
                                        && typeValue is JsonValue tv
                                        && tv.TryGetValue<string>(out var type))
                                    {
                                        sub["type"] = type.ToLowerInvariant();
                                    }
                                });
                            }
                            catch (JsonException)
                            {
                                return raw;
                            }
                        }

                        // Determine which format was used and preserve it.
                        JsonElement? parameters = null;
                        JsonElement? parametersJsonSchema = null;

                        if (fd.Parameters is { } parametersRaw)
                        {
                            // Old format.
                            parameters = Transform(fd.Parameters);
                        }
                        else if (fd.ParametersJsonSchema is { } parametersJsonSchemaRaw)
                        {
                            // New format.
                            parametersJsonSchema = Transform(fd.ParametersJsonSchema);
                        }

                        tools.Add(new AxonHub.Llm.Tool
                        {
                            Type = ToolTypes.Function,
                            Function = new Function
                            {
                                Name = fd.Name ?? string.Empty,
                                Description = fd.Description,
                                Parameters = parameters,
                                ParametersJsonSchema = parametersJsonSchema,
                            },
                        });
                    }
                }

                // Handle Google Search tool.
                if (tool.GoogleSearch is not null)
                {
                    tools.Add(new AxonHub.Llm.Tool
                    {
                        Type = ToolTypes.GoogleSearch,
                        Google = new GoogleTools { Search = new AxonHub.Llm.GoogleSearch() },
                    });
                }

                // Handle Code Execution tool.
                if (tool.CodeExecution is not null)
                {
                    tools.Add(new AxonHub.Llm.Tool
                    {
                        Type = ToolTypes.GoogleCodeExecution,
                        Google = new GoogleTools { CodeExecution = new AxonHub.Llm.GoogleCodeExecution() },
                    });
                }

                // Handle URL Context tool.
                if (tool.UrlContext is not null)
                {
                    tools.Add(new AxonHub.Llm.Tool
                    {
                        Type = ToolTypes.GoogleUrlContext,
                        Google = new GoogleTools { UrlContext = new AxonHub.Llm.GoogleUrlContext() },
                    });
                }
            }

            chatReq.Tools = tools;
        }

        // Convert tool config.
        if (geminiReq.ToolConfig?.FunctionCallingConfig is { } fcc)
        {
            chatReq.ToolChoice = GeminiConvert.ConvertGeminiFunctionCallingConfigToToolChoice(fcc);
        }

        // Convert safety settings to TransformerMetadata.
        if (geminiReq.SafetySettings is { Count: > 0 })
        {
            chatReq.TransformerMetadata ??= new Dictionary<string, JsonElement>();
            chatReq.TransformerMetadata[GeminiConvert.TransformerMetadataKeySafetySettings] =
                JsonSerializer.SerializeToElement(geminiReq.SafetySettings, LlmJson.Options);
        }

        // Convert image config to TransformerMetadata.
        if (geminiReq.GenerationConfig?.ImageConfig is { } imageConfig)
        {
            chatReq.TransformerMetadata ??= new Dictionary<string, JsonElement>();
            chatReq.TransformerMetadata[GeminiConvert.TransformerMetadataKeyImageConfig] =
                JsonSerializer.SerializeToElement(imageConfig, LlmJson.Options);
        }

        return chatReq;
    }

    /// <summary>Port of convertGeminiContentToLLMMessages (inbound_convert.go:249-310).</summary>
    public static List<Message>? ConvertGeminiContentToLLMMessages(Content? content, List<Content>? previousContents)
    {
        if (content?.Parts is not { Count: > 0 } parts)
        {
            return null;
        }

        var hasFunctionResponse = parts.Any(part => part?.FunctionResponse is not null);
        if (!hasFunctionResponse || parts.Count == 1)
        {
            var msg = ConvertGeminiContentToLLMMessage(content, previousContents);
            return msg is null ? null : [msg];
        }

        var messages = new List<Message>(parts.Count);
        var regularParts = new List<Part>(parts.Count);

        void FlushRegularParts()
        {
            if (regularParts.Count == 0)
            {
                return;
            }

            var msg = ConvertGeminiContentToLLMMessage(
                new Content { Role = content.Role, Parts = regularParts }, previousContents);
            if (msg is not null)
            {
                messages.Add(msg);
            }

            regularParts = [];
        }

        foreach (var part in parts)
        {
            if (part is null)
            {
                continue;
            }

            if (part.FunctionResponse is null)
            {
                regularParts.Add(part);
                continue;
            }

            FlushRegularParts();
            var msg = ConvertGeminiContentToLLMMessage(
                new Content { Role = content.Role, Parts = [part] }, previousContents);
            if (msg is not null)
            {
                messages.Add(msg);
            }
        }

        FlushRegularParts();

        return messages;
    }

    /// <summary>Port of geminiJSONSchemaResponseFormat (inbound_convert.go:312-325).</summary>
    public static ResponseFormat GeminiJsonSchemaResponseFormat(JsonElement schema)
    {
        var wrapper = new JsonObject
        {
            ["name"] = "gemini_response",
            ["schema"] = JsonNode.Parse(schema.GetRawText()),
        };

        return new ResponseFormat
        {
            Type = "json_schema",
            JsonSchema = JsonSerializer.SerializeToElement(wrapper, LlmJson.Options),
        };
    }

    /// <summary>Port of convertGeminiThinkingConfigToGeminiOpenAIExtraBody (inbound_convert.go:327-356).</summary>
    public static JsonElement? ConvertGeminiThinkingConfigToGeminiOpenAIExtraBody(ThinkingConfig? thinkingConfig)
    {
        if (thinkingConfig is null)
        {
            return null;
        }

        var extraThinkingConfig = new OpenAI.ThinkingConfig
        {
            IncludeThoughts = thinkingConfig.IncludeThoughts,
        };

        if (thinkingConfig.ThinkingBudget is { } budget)
        {
            extraThinkingConfig.ThinkingBudget = new OpenAI.ThinkingBudget { IntValue = budget };
        }

        if (thinkingConfig.ThinkingLevel is { Length: > 0 } thinkingLevel)
        {
            var level = thinkingLevel.ToLowerInvariant();
            if (level == "minimal")
            {
                level = "low";
            }

            extraThinkingConfig.ThinkingLevel = level;
        }

        var extraBody = new OpenAI.ExtraBody
        {
            Google = new OpenAI.GoogleExtraBody { ThinkingConfig = extraThinkingConfig },
        };

        return JsonSerializer.SerializeToElement(extraBody, LlmJson.Options);
    }

    /// <summary>Port of convertGeminiContentToLLMMessage (inbound_convert.go:359-534).</summary>
    public static Message? ConvertGeminiContentToLLMMessage(Content? content, List<Content>? previousContents)
    {
        if (content?.Parts is not { Count: > 0 } parts)
        {
            return null;
        }

        var msg = new Message
        {
            Role = GeminiConvert.ConvertGeminiRoleToLlmRole(content.Role ?? string.Empty),
        };

        var textParts = new List<MessageContentPart>();
        var toolCalls = new List<ToolCall>();
        string? reasoningContent = null;

        foreach (var part in parts)
        {
            if (part is null)
            {
                continue;
            }

            if (msg.ReasoningSignature is null && part.ThoughtSignature is { Length: > 0 } thoughtSignature)
            {
                msg.ReasoningSignature = thoughtSignature;
            }

            if (part.Text is { Length: > 0 } text)
            {
                if (part.Thought)
                {
                    reasoningContent = text;
                }
                else
                {
                    textParts.Add(new MessageContentPart { Type = "text", Text = text });
                }
            }
            else if (part.InlineData is { } inlineData)
            {
                // Convert inline data based on MIME type.
                var dataUrl = Xurl.BuildDataUrl(inlineData.MIMEType ?? string.Empty, inlineData.Data ?? string.Empty, true);
                var mimeType = inlineData.MIMEType ?? string.Empty;

                if (GeminiMime.IsDocumentMimeType(mimeType))
                {
                    // Document type (PDF, Word, etc.)
                    textParts.Add(new MessageContentPart
                    {
                        Type = "document",
                        Document = new DocumentUrl { Url = dataUrl, MimeType = inlineData.MIMEType },
                    });
                }
                else if (GeminiMime.IsVideoMimeType(mimeType))
                {
                    textParts.Add(new MessageContentPart
                    {
                        Type = "video_url",
                        VideoUrl = new VideoURL { URL = dataUrl, MIMEType = inlineData.MIMEType },
                    });
                }
                else if (GeminiMime.IsAudioMimeType(mimeType))
                {
                    textParts.Add(new MessageContentPart
                    {
                        Type = "input_audio",
                        InputAudio = new InputAudio
                        {
                            Format = GeminiConvert.AudioMimeTypeToFormat(mimeType),
                            Data = inlineData.Data ?? string.Empty,
                            MimeType = inlineData.MIMEType,
                        },
                    });
                }
                else
                {
                    // Image type.
                    textParts.Add(new MessageContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ImageUrl { Url = dataUrl, MimeType = inlineData.MIMEType },
                    });
                }
            }
            else if (part.FileData is { } fileData)
            {
                // Convert file data based on MIME type or URL extension.
                var mimeType = GeminiMime.MediaMimeType(
                    fileData.MIMEType,
                    fileData.FileURI ?? string.Empty,
                    static mediaType =>
                        GeminiMime.IsDocumentMimeType(mediaType) || GeminiMime.IsVideoMimeType(mediaType) ||
                        GeminiMime.IsAudioMimeType(mediaType) || mediaType.StartsWith("image/", StringComparison.Ordinal));

                if (GeminiMime.IsDocumentMimeType(mimeType))
                {
                    // Document type (PDF, Word, etc.)
                    textParts.Add(new MessageContentPart
                    {
                        Type = "document",
                        Document = new DocumentUrl { Url = fileData.FileURI ?? string.Empty, MimeType = mimeType },
                    });
                }
                else if (GeminiMime.IsVideoMimeType(mimeType))
                {
                    textParts.Add(new MessageContentPart
                    {
                        Type = "video_url",
                        VideoUrl = new VideoURL { URL = fileData.FileURI ?? string.Empty, MIMEType = mimeType },
                    });
                }
                else if (GeminiMime.IsAudioMimeType(mimeType))
                {
                    textParts.Add(new MessageContentPart
                    {
                        Type = "input_audio",
                        InputAudio = new InputAudio
                        {
                            Format = GeminiConvert.AudioMimeTypeToFormat(mimeType),
                            Url = fileData.FileURI,
                            MimeType = mimeType,
                        },
                    });
                }
                else
                {
                    // Image type.
                    textParts.Add(new MessageContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ImageUrl { Url = fileData.FileURI ?? string.Empty, MimeType = mimeType },
                    });
                }
            }
            else if (part.FunctionCall is { } functionCall)
            {
                var argsJson = functionCall.Args is not null
                    ? GoJson.MarshalToString(functionCall.Args)
                    : "{}";

                var tc = new ToolCall
                {
                    Id = functionCall.Id,
                    Type = ToolTypes.Function,
                    Function = new AxonHub.Llm.FunctionCall
                    {
                        Name = functionCall.Name ?? string.Empty,
                        Arguments = argsJson,
                    },
                };
                GeminiThoughtSignatures.SetInboundToolCallThoughtSignature(tc, part.ThoughtSignature);
                toolCalls.Add(tc);
            }
            else if (part.FunctionResponse is { } functionResponse)
            {
                // Function response is a separate message in unified format.
                var responseJson = GoJson.MarshalToString(functionResponse.Response);

                // If FunctionResponse ID is empty, find the matching function
                // call ID from previous contents.
                var functionResponseId = functionResponse.Id is { Length: > 0 } id
                    ? id
                    : FindMatchingFunctionCallId(functionResponse.Name ?? string.Empty, previousContents);

                return new Message
                {
                    Role = "tool",
                    ToolCallId = functionResponseId,
                    ToolCallName = functionResponse.Name,
                    Content = new MessageContent { Content = responseJson },
                };
            }
        }

        // Set content.
        if (textParts.Count == 1 && textParts[0].Type == "text")
        {
            msg.Content = new MessageContent { Content = textParts[0].Text };
        }
        else if (textParts.Count > 0)
        {
            msg.Content = new MessageContent { MultipleContent = textParts };
        }

        // Set tool calls.
        if (toolCalls.Count > 0)
        {
            msg.ToolCalls = toolCalls;
        }

        // Set reasoning content.
        if (reasoningContent is { Length: > 0 })
        {
            msg.ReasoningContent = reasoningContent;
        }

        return msg;
    }

    /// <summary>Port of citationMetadataFromLLMAnnotations (inbound_convert.go:536-562).</summary>
    public static CitationMetadata? CitationMetadataFromLLMAnnotations(List<Annotation>? annotations)
    {
        if (annotations is null)
        {
            return null;
        }

        var citations = new List<Citation>(annotations.Count);
        foreach (var ann in annotations)
        {
            if (ann.UrlCitation is not { Url.Length: > 0 } urlCitation)
            {
                continue;
            }

            var citation = new Citation
            {
                URI = urlCitation.Url,
                Title = urlCitation.Title,
            };
            if (ann.StartIndex is { } startIndex)
            {
                citation.StartIndex = startIndex;
            }

            if (ann.EndIndex is { } endIndex)
            {
                citation.EndIndex = endIndex;
            }

            citations.Add(citation);
        }

        return citations.Count == 0 ? null : new CitationMetadata { Citations = citations };
    }

    /// <summary>Port of convertLLMToGeminiResponse (inbound_convert.go:566-589).</summary>
    public static GenerateContentResponse ConvertLLMToGeminiResponse(LlmResponse chatResp, bool isStream)
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = chatResp.Id,
            ModelVersion = chatResp.Model,
        };

        // Convert choices to candidates.
        var candidates = new List<Candidate>(chatResp.Choices.Count);
        foreach (var choice in chatResp.Choices)
        {
            var candidate = ConvertLLMChoiceToGeminiCandidate(choice, isStream);

            // Extract GroundingMetadata from Choice.TransformerMetadata if present.
            if (Xmap.GetPtr<GroundingMetadata>(
                    choice.TransformerMetadata,
                    GeminiOutboundConvert.TransformerMetadataKeyGroundingMetadata) is { } groundingMetadata)
            {
                candidate.GroundingMetadata = groundingMetadata;
            }

            candidates.Add(candidate);
        }

        resp.Candidates = candidates;
        resp.UsageMetadata = GeminiConvert.ConvertToGeminiUsage(chatResp.Usage);

        return resp;
    }

    /// <summary>Port of convertLLMChoiceToGeminiCandidate (inbound_convert.go:593-732).</summary>
    public static Candidate ConvertLLMChoiceToGeminiCandidate(Choice choice, bool isStream)
    {
        var candidate = new Candidate
        {
            Index = choice.Index,
        };

        var msg = isStream
            ? choice.Delta ?? choice.Message
            : choice.Message ?? choice.Delta;

        if (msg is not null)
        {
            var content = new Content
            {
                Role = "model",
            };

            var parts = new List<Part>();
            Part? lastPart = null;
            Part? firstFunctionCallPart = null;

            // Add reasoning content (thinking) first if present.
            if (msg.ReasoningContent is { Length: > 0 } reasoningContent)
            {
                var p = new Part
                {
                    Text = reasoningContent,
                    Thought = true,
                };
                parts.Add(p);
                lastPart = p;
            }

            // Add text content.
            if (msg.Content?.Content is { Length: > 0 } textContent)
            {
                var p = new Part { Text = textContent };
                parts.Add(p);
                lastPart = p;
            }
            else if (msg.Content?.MultipleContent is { Count: > 0 } multipleContent)
            {
                foreach (var part in multipleContent)
                {
                    switch (part.Type)
                    {
                        case "text":
                            if (part.Text is { } text)
                            {
                                var p = new Part { Text = text };
                                parts.Add(p);
                                lastPart = p;
                            }

                            break;
                        case "image_url":
                            // Handle image_url type.
                            if (part.ImageUrl is { Url.Length: > 0 } imageUrl)
                            {
                                var geminiPart = GeminiConvert.ConvertImageUrlToGeminiPart(imageUrl);
                                if (geminiPart is not null)
                                {
                                    parts.Add(geminiPart);
                                    lastPart = geminiPart;
                                }
                            }

                            break;
                        case "video_url":
                            if (part.VideoUrl is { URL.Length: > 0 } videoUrl)
                            {
                                var geminiPart = GeminiConvert.ConvertVideoUrlToGeminiPart(videoUrl);
                                if (geminiPart is not null)
                                {
                                    parts.Add(geminiPart);
                                    lastPart = geminiPart;
                                }
                            }

                            break;
                        case "document":
                            // Handle document type (PDF, Word, etc.)
                            if (part.Document is { Url.Length: > 0 } document)
                            {
                                var geminiPart = GeminiConvert.ConvertDocumentUrlToGeminiPart(document);
                                if (geminiPart is not null)
                                {
                                    parts.Add(geminiPart);
                                    lastPart = geminiPart;
                                }
                            }

                            break;
                    }
                }
            }

            var hasToolCallThoughtSignature = false;

            foreach (var toolCall in msg.ToolCalls ?? [])
            {
                Dictionary<string, JsonElement>? args = null;
                if (toolCall.Function.Arguments is { Length: > 0 })
                {
                    try
                    {
                        args = LlmJson.Deserialize<Dictionary<string, JsonElement>>(toolCall.Function.Arguments);
                    }
                    catch (JsonException)
                    {
                        args = null;
                    }
                }

                var part = new Part
                {
                    FunctionCall = new GeminiFunctionCall
                    {
                        Id = toolCall.Id,
                        Name = toolCall.Function.Name,
                        Args = args,
                    },
                };
                if (GeminiThoughtSignatures.GetInboundGeminiToolCallThoughtSignature(toolCall) is { } signature)
                {
                    part.ThoughtSignature = signature;
                    hasToolCallThoughtSignature = true;
                }

                parts.Add(part);

                lastPart = part;
                firstFunctionCallPart ??= part;
            }

            if (!hasToolCallThoughtSignature && msg.ReasoningSignature is not null)
            {
                if (firstFunctionCallPart is not null)
                {
                    firstFunctionCallPart.ThoughtSignature = msg.ReasoningSignature;
                }
                else if (lastPart is not null)
                {
                    lastPart.ThoughtSignature = msg.ReasoningSignature;
                }
            }

            content.Parts = parts;
            candidate.Content = content;
        }

        if (Xmap.GetPtr<GroundingMetadata>(
                choice.TransformerMetadata,
                GeminiOutboundConvert.TransformerMetadataKeyGroundingMetadata) is { } gm)
        {
            candidate.GroundingMetadata = gm;
        }
        else if (msg?.Annotations is { Count: > 0 })
        {
            candidate.CitationMetadata = CitationMetadataFromLLMAnnotations(msg.Annotations);
        }

        // Convert finish reason.
        candidate.FinishReason = GeminiConvert.ConvertLlmFinishReasonToGemini(choice.FinishReason);

        return candidate;
    }

    /// <summary>Port of findMatchingFunctionCallID (inbound_convert.go:736-754).</summary>
    public static string FindMatchingFunctionCallId(string functionName, List<Content>? previousContents)
    {
        if (previousContents is null)
        {
            return string.Empty;
        }

        // Search from the end to the beginning to find the most recent
        // matching function call.
        for (var i = previousContents.Count - 1; i >= 0; i--)
        {
            var content = previousContents[i];
            if (content?.Parts is not { } parts)
            {
                continue;
            }

            // Look through all parts in this content in reverse order to find
            // the most recent function call.
            for (var j = parts.Count - 1; j >= 0; j--)
            {
                var part = parts[j];
                if (part?.FunctionCall is { Name: var name, Id.Length: > 0 } functionCall && name == functionName)
                {
                    return functionCall.Id;
                }
            }
        }

        return string.Empty;
    }
}
