using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Gemini.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>Port of the request/response conversion helpers (llm/transformer/gemini/outbound_convert.go).</summary>
public static class GeminiOutboundConvert
{
    /// <summary>Port of convertLLMToGeminiRequest (outbound_convert.go:19-21).</summary>
    public static GenerateContentRequest ConvertLlmToGeminiRequest(LlmRequest chatReq)
        => ConvertLlmToGeminiRequestWithConfig(chatReq, null);

    /// <summary>Port of convertLLMToGeminiRequestWithConfig (outbound_convert.go:24-321).</summary>
    public static GenerateContentRequest ConvertLlmToGeminiRequestWithConfig(LlmRequest chatReq, GeminiOutboundConfig? config)
    {
        var req = new GenerateContentRequest();

        // Convert generation config
        var gc = new GenerationConfig();
        var hasGenerationConfig = false;

        if (chatReq.MaxTokens is { } maxTokens)
        {
            gc.MaxOutputTokens = maxTokens;
            hasGenerationConfig = true;
        }
        else if (chatReq.MaxCompletionTokens is { } maxCompletionTokens)
        {
            gc.MaxOutputTokens = maxCompletionTokens;
            hasGenerationConfig = true;
        }

        if (chatReq.Temperature is { } temperature)
        {
            gc.Temperature = temperature;
            hasGenerationConfig = true;
        }

        if (chatReq.TopP is { } topP)
        {
            gc.TopP = topP;
            hasGenerationConfig = true;
        }

        if (chatReq.PresencePenalty is { } presencePenalty)
        {
            gc.PresencePenalty = presencePenalty;
            hasGenerationConfig = true;
        }

        if (chatReq.FrequencyPenalty is { } frequencyPenalty)
        {
            gc.FrequencyPenalty = frequencyPenalty;
            hasGenerationConfig = true;
        }

        if (chatReq.Seed is { } seed)
        {
            gc.Seed = seed;
            hasGenerationConfig = true;
        }

        if (chatReq.Stop is { } stop)
        {
            if (stop.Single is { } single)
            {
                gc.StopSequences = [single];
            }
            else if (stop.Multiple is { Count: > 0 })
            {
                gc.StopSequences = stop.Multiple;
            }

            hasGenerationConfig = true;
        }

        // Priority 1: Parse ExtraBody from llm.Request (higher priority)
        var hasExtraBodyThinkingConfig = false;

        if (chatReq.ExtraBody is { } extraBodyRaw)
        {
            var extraBody = GeminiExtraBodyParser.ParseExtraBody(extraBodyRaw);
            if (extraBody?.Google?.ThinkingConfig is { } geminiOaiThinking)
            {
                // Convert geminioai.ThinkingConfig to gemini.ThinkingConfig
                var geminiThinkingConfig = new ThinkingConfig
                {
                    IncludeThoughts = geminiOaiThinking.IncludeThoughts,
                };

                // Priority 1: Use ThinkingLevel if present (takes absolute priority)
                if (geminiOaiThinking.ThinkingLevel is { Length: > 0 } level)
                {
                    // Map "minimal" to "low" for consistency
                    if (level.Equals("minimal", StringComparison.OrdinalIgnoreCase))
                    {
                        level = "low";
                    }

                    geminiThinkingConfig.ThinkingLevel = level;
                    // Don't set ThinkingBudget when ThinkingLevel is present
                }
                else if (geminiOaiThinking.ThinkingBudget is { } thinkingBudget)
                {
                    // Priority 2: Use ThinkingBudget if no level
                    if (thinkingBudget.IntValue is { } intValue)
                    {
                        // Integer budget: use as ThinkingBudget
                        geminiThinkingConfig.ThinkingBudget = intValue;
                    }
                    else if (thinkingBudget.StringValue is { } strValueRaw)
                    {
                        // String budget: convert to ThinkingLevel for standard values
                        var strVal = strValueRaw.ToLowerInvariant();
                        geminiThinkingConfig.ThinkingLevel = strVal switch
                        {
                            "low" or "minimal" => "low",
                            "medium" => "medium",
                            "high" => "high",
                            // Unknown string value, use as-is
                            _ => strVal,
                        };
                    }
                }

                gc.ThinkingConfig = geminiThinkingConfig;
                hasGenerationConfig = true;
                hasExtraBodyThinkingConfig = true;
            }
        }

        // Convert reasoning effort to thinking config
        // Priority: ExtraBody > ReasoningBudget > ReasoningEffort > default
        if (!hasExtraBodyThinkingConfig)
        {
            if (chatReq.ReasoningBudget is { } reasoningBudget)
            {
                // Priority 1: Use ReasoningBudget if provided
                // Prefer ThinkingLevel for Gemini 3 models when budget falls within standard ranges
                if (GeminiConvert.ShouldUseThinkingLevelForBudget(chatReq.Model, reasoningBudget))
                {
                    // Convert budget to effort level for standard ranges
                    gc.ThinkingConfig = new ThinkingConfig
                    {
                        IncludeThoughts = true,
                        ThinkingLevel = GeminiConvert.ThinkingBudgetToReasoningEffort(reasoningBudget),
                    };
                }
                else
                {
                    // Use ThinkingBudget for non-standard values
                    gc.ThinkingConfig = new ThinkingConfig
                    {
                        IncludeThoughts = true,
                        // Gemini max thinking budget is 24576
                        ThinkingBudget = Math.Min(reasoningBudget, 24576),
                    };
                }

                hasGenerationConfig = true;
            }
            else if (chatReq.ReasoningEffort is { Length: > 0 } reasoningEffort)
            {
                // Priority 2: Convert from ReasoningEffort if provided
                // Use ThinkingLevel for standard effort values (low, medium, high)
                // to preserve the original format when possible
                var thinkingConfig = new ThinkingConfig
                {
                    IncludeThoughts = true,
                };

                // For standard effort levels, use ThinkingLevel
                switch (reasoningEffort.ToLowerInvariant())
                {
                    case "none" or "low" or "medium" or "high":
                        thinkingConfig.ThinkingLevel = reasoningEffort;
                        break;
                    case "xhigh":
                        // "xhigh" comes from Anthropic "max"; Gemini's highest level is "high".
                        thinkingConfig.ThinkingLevel = "high";
                        break;
                    default:
                        // For non-standard effort values, convert to budget
                        thinkingConfig.ThinkingBudget = Math.Min(
                            GeminiConvert.ReasoningEffortToThinkingBudgetWithConfig(reasoningEffort, config),
                            24576);
                        break;
                }

                gc.ThinkingConfig = thinkingConfig;
                hasGenerationConfig = true;
            }
        }

        // Convert modalities to responseModalities
        if (chatReq.Modalities is { Count: > 0 })
        {
            gc.ResponseModalities = GeminiConvert.ConvertLlmModalitiesToGemini(chatReq.Modalities);
            hasGenerationConfig = true;
        }

        // Convert ResponseFormat to ResponseSchema and ResponseMIMEType
        if (chatReq.ResponseFormat is { } responseFormat)
        {
            if (responseFormat.Type == "json_schema" && responseFormat.JsonSchema is { } jsonSchema)
            {
                gc.ResponseJsonSchema = ExtractJsonSchema(jsonSchema);
                gc.ResponseMIMEType = "application/json";
                hasGenerationConfig = true;
            }
            else if (responseFormat.Type == "json_object")
            {
                gc.ResponseMIMEType = "application/json";
                hasGenerationConfig = true;
            }
        }

        if (hasGenerationConfig)
        {
            req.GenerationConfig = gc;
        }

        // Convert messages
        Content? systemInstruction = null;

        var contents = new List<Content>();

        foreach (var msg in chatReq.Messages)
        {
            switch (msg.Role)
            {
                case "system" or "developer":
                    // Collect system and developer messages into system instruction
                    var systemParts = GeminiConvert.ExtractPartsFromLlmMessage(msg);
                    if (systemParts.Count > 0)
                    {
                        if (systemInstruction == null)
                        {
                            systemInstruction = new Content
                            {
                                Parts = systemParts,
                            };
                        }
                        else
                        {
                            // Append to existing system instruction
                            systemInstruction.Parts!.AddRange(systemParts);
                        }
                    }

                    break;

                case "tool":
                    // Tool response - need to find the corresponding function call
                    // Group consecutive tool messages into a single Content entry
                    var toolContent = ConvertLlmToolResultToGeminiContent(msg, contents);
                    if (IsPreviousContentToolResponse(contents))
                    {
                        contents[^1].Parts!.AddRange(toolContent.Parts!);
                    }
                    else
                    {
                        contents.Add(toolContent);
                    }

                    break;

                default:
                    var content = ConvertLlmMessageToGeminiContent(msg);
                    if (content != null)
                    {
                        contents.Add(content);
                    }

                    break;
            }
        }

        req.SystemInstruction = systemInstruction;
        req.Contents = contents;

        // Convert tools
        if (chatReq.Tools is { Count: > 0 } chatTools)
        {
            var tools = new List<Tool>();
            var functionDeclarations = new List<FunctionDeclaration>();

            Tool? functionTool = null;

            foreach (var tool in chatTools)
            {
                switch (tool.Type)
                {
                    case "function":
                        var fd = new FunctionDeclaration
                        {
                            Name = tool.Function.Name,
                            Description = tool.Function.Description,
                        };

                        if (tool.Function.ParametersJsonSchema is { } parametersJsonSchema)
                        {
                            fd.ParametersJsonSchema = parametersJsonSchema;
                        }
                        else if (tool.Function.Parameters is { } parameters)
                        {
                            fd.ParametersJsonSchema = parameters;
                        }

                        functionDeclarations.Add(fd);

                        if (functionTool == null)
                        {
                            functionTool = new Tool();
                            tools.Add(functionTool);
                        }

                        break;

                    case ToolTypes.GoogleSearch or ToolTypes.WebSearch:
                        if (tool.Type == ToolTypes.GoogleSearch && tool.Google?.Search is null)
                        {
                            break;
                        }

                        tools.Add(new Tool { GoogleSearch = new GoogleSearch() });
                        break;

                    case ToolTypes.GoogleCodeExecution:
                        if (tool.Google?.CodeExecution != null)
                        {
                            tools.Add(new Tool { CodeExecution = new CodeExecution() });
                        }

                        break;

                    case ToolTypes.GoogleUrlContext:
                        if (tool.Google?.UrlContext != null)
                        {
                            tools.Add(new Tool { UrlContext = new UrlContext() });
                        }

                        break;
                }
            }

            if (functionTool != null)
            {
                functionTool.FunctionDeclarations = functionDeclarations;
            }

            if (tools.Count > 0)
            {
                req.Tools = tools;
            }
        }

        // Convert tool choice
        if (chatReq.ToolChoice != null)
        {
            req.ToolConfig = GeminiConvert.ConvertLlmToolChoiceToGeminiToolConfig(chatReq.ToolChoice);
        }

        if (HasStrictFunctionTool(chatReq.Tools))
        {
            if (req.ToolConfig == null)
            {
                req.ToolConfig = new ToolConfig
                {
                    FunctionCallingConfig = new FunctionCallingConfig { Mode = "VALIDATED" },
                };
            }
            else if (req.ToolConfig.FunctionCallingConfig!.Mode == "AUTO")
            {
                req.ToolConfig.FunctionCallingConfig.Mode = "VALIDATED";
            }
        }

        // Convert safety settings from TransformerMetadata
        if (GeminiConvert.ExtractSafetySettingsFromMetadata(chatReq.TransformerMetadata) is { Count: > 0 } safetySettings)
        {
            req.SafetySettings = safetySettings;
        }

        // Convert image config from TransformerMetadata
        if (GeminiConvert.ExtractImageConfigFromMetadata(chatReq.TransformerMetadata) is { } imageConfig)
        {
            req.GenerationConfig ??= new GenerationConfig();
            req.GenerationConfig.ImageConfig = imageConfig;
        }

        return req;
    }

    /// <summary>Port of hasStrictFunctionTool (outbound_convert.go:323-331).</summary>
    public static bool HasStrictFunctionTool(List<AxonHub.Llm.Tool>? tools)
    {
        if (tools is null)
        {
            return false;
        }

        foreach (var tool in tools)
        {
            if (tool.Type == ToolTypes.Function && tool.Function.Strict == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of convertLLMMessageToGeminiContent (outbound_convert.go:334-482).</summary>
    public static Content? ConvertLlmMessageToGeminiContent(Message? msg)
    {
        if (msg == null)
        {
            return null;
        }

        var content = new Content
        {
            Role = GeminiConvert.ConvertLlmRoleToGeminiRole(msg.Role ?? string.Empty),
        };

        var parts = new List<Part>();

        Part? firstFunctionCallPart = null;
        Part? lastPart = null;

        // Add reasoning content (thinking) first if present.
        var reasoningContent = msg.ReasoningContent;

        if (reasoningContent is { Length: > 0 })
        {
            var p = new Part
            {
                Text = reasoningContent,
                Thought = true,
            };
            parts.Add(p);
            lastPart = p;
        }

        // Add text content
        if (msg.Content?.Content is { Length: > 0 } textContent)
        {
            var p = new Part { Text = textContent };
            parts.Add(p);
            lastPart = p;
        }
        else if (msg.Content?.MultipleContent is { } multipleContent)
        {
            foreach (var part in multipleContent)
            {
                switch (part.Type)
                {
                    case "text":
                        if (part.Text != null)
                        {
                            var p = new Part { Text = part.Text };
                            parts.Add(p);
                            lastPart = p;
                        }

                        break;
                    case "image_url":
                        // Handle image_url type
                        if (part.ImageUrl?.Url is { Length: > 0 })
                        {
                            if (GeminiConvert.ConvertImageUrlToGeminiPart(part.ImageUrl) is { } geminiPart)
                            {
                                parts.Add(geminiPart);
                                lastPart = geminiPart;
                            }
                        }

                        break;
                    case "video_url":
                        if (part.VideoUrl?.URL is { Length: > 0 })
                        {
                            if (GeminiConvert.ConvertVideoUrlToGeminiPart(part.VideoUrl) is { } geminiPart)
                            {
                                parts.Add(geminiPart);
                                lastPart = geminiPart;
                            }
                        }

                        break;
                    case "document":
                        // Handle document type (PDF, Word, etc.)
                        if (part.Document?.Url is { Length: > 0 })
                        {
                            if (GeminiConvert.ConvertDocumentUrlToGeminiPart(part.Document) is { } geminiPart)
                            {
                                parts.Add(geminiPart);
                                lastPart = geminiPart;
                            }
                        }

                        break;
                    case "input_audio":
                        if (part.InputAudio != null && (part.InputAudio.Data.Length > 0 || part.InputAudio.Url is { Length: > 0 }))
                        {
                            if (GeminiConvert.ConvertAudioToGeminiPart(part.InputAudio) is { } geminiPart)
                            {
                                parts.Add(geminiPart);
                                lastPart = geminiPart;
                            }
                        }

                        break;
                }
            }
        }

        // https://ai.google.dev/gemini-api/docs/thought-signatures#model-behavior
        // Gemini 3 Pro, Gemini 3 Pro Image and Gemini 2.5 models behave differently with thought signatures.
        // For Gemini 3 Pro Image see the thinking process section of the image generation guide.
        // Gemini 3 Pro and Gemini 2.5 models behave differently with thought signatures in function calls:
        //     If there are function calls in a response,
        //         Gemini 3 Pro will always have the signature on the first function call part. It is mandatory to return that part.
        //         Gemini 2.5 will have the signature in the first part (regardless of type). It is optional to return that part.
        //     If there are no function calls in a response,
        //         Gemini 3 Pro will have the signature on the last part if the model generates a thought.
        //         Gemini 2.5 won't have a signature in any part.

        var hasToolCallThoughtSignature = false;

        // Add tool calls
        foreach (var toolCall in msg.ToolCalls ?? [])
        {
            Dictionary<string, JsonElement>? args = null;
            if (toolCall.Function.Arguments.Length > 0)
            {
                try
                {
                    args = LlmJson.Deserialize<Dictionary<string, JsonElement>>(toolCall.Function.Arguments);
                }
                catch (JsonException)
                {
                    // Go: _ = json.Unmarshal(...) leaves args nil on failure.
                }
            }

            var part = new Part
            {
                FunctionCall = new FunctionCall
                {
                    Id = toolCall.Id,
                    Name = toolCall.Function.Name,
                    Args = args,
                },
            };

            if (GeminiThoughtSignatures.GetOutboundGeminiToolCallThoughtSignature(toolCall) is { } signature)
            {
                part.ThoughtSignature = signature;
                hasToolCallThoughtSignature = true;
            }

            parts.Add(part);

            lastPart = part;
            firstFunctionCallPart ??= part;
        }

        if (!hasToolCallThoughtSignature)
        {
            // https://ai.google.dev/gemini-api/docs/gemini-3#migrating_from_other_models
            // If there are tool calls but no thought signature, use a default one.
            // This field is not compatible with OpenAI sdk, so we use the default value.
            // We try the best to support this fields to keep this fields in the chat conversions, so we use the ReasoningSignature to hold the field,
            // And this field will be preserved during claude code trace, will not degrade the gemini model performance.
            var msgThoughtSignature = GeminiSignatures.DecodeGeminiThoughtSignature(msg.ReasoningSignature);

            if ((msg.ToolCalls is { Count: > 0 } || msg.ReasoningContent != null) && msgThoughtSignature == null)
            {
                msgThoughtSignature = GeminiThoughtSignatures.ContextEngineeringThoughtSignature;
            }

            if (msgThoughtSignature != null && (firstFunctionCallPart != null || lastPart != null))
            {
                if (firstFunctionCallPart != null)
                {
                    firstFunctionCallPart.ThoughtSignature = msgThoughtSignature;
                }
                else
                {
                    lastPart!.ThoughtSignature = msgThoughtSignature;
                }
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        content.Parts = parts;

        return content;
    }

    /// <summary>Port of convertLLMToolResultToGeminiContent (outbound_convert.go:485-518).</summary>
    public static Content ConvertLlmToolResultToGeminiContent(Message msg, List<Content> contents)
    {
        var content = new Content
        {
            Role = "user", // Function responses come from user role in Gemini
        };

        Dictionary<string, JsonElement>? responseData = null;
        if (msg.Content?.Content is { } toolContent)
        {
            try
            {
                responseData = LlmJson.Deserialize<Dictionary<string, JsonElement>>(toolContent);
            }
            catch (JsonException)
            {
                // Go: _ = json.Unmarshal(...) leaves responseData nil on failure.
            }
        }

        if (responseData == null)
        {
            responseData = new Dictionary<string, JsonElement>
            {
                ["result"] = JsonSerializer.SerializeToElement(msg.Content?.Content ?? string.Empty),
            };
        }

        var toolCallId = msg.ToolCallId ?? string.Empty;

        // Anthropic's tool result doesn't have name, so we need to find it by tool call id.
        var toolCallName = msg.ToolCallName ?? string.Empty;
        if (toolCallName.Length == 0)
        {
            toolCallName = FindToolNameByToolCallId(contents, toolCallId);
        }

        var fp = new FunctionResponse
        {
            Id = toolCallId,
            Name = toolCallName,
            Response = responseData,
        };

        content.Parts = [new Part { FunctionResponse = fp }];

        return content;
    }

    /// <summary>Port of findToolNameByToolCallID (outbound_convert.go:520-530).</summary>
    public static string FindToolNameByToolCallId(List<Content> contents, string id)
    {
        foreach (var content in contents)
        {
            foreach (var part in content.Parts ?? [])
            {
                if (part.FunctionCall is { } functionCall && functionCall.Id == id)
                {
                    return functionCall.Name ?? string.Empty;
                }
            }
        }

        return "";
    }

    /// <summary>
    /// Port of isPreviousContentToolResponse (outbound_convert.go:532-545):
    /// whether the last content entry is a tool response (user role with
    /// functionResponse parts) for grouping consecutive tool messages.
    /// </summary>
    public static bool IsPreviousContentToolResponse(List<Content> contents)
    {
        if (contents.Count == 0)
        {
            return false;
        }

        var lastContent = contents[^1];
        if (lastContent.Role != "user" || lastContent.Parts is not { Count: > 0 })
        {
            return false;
        }

        return lastContent.Parts[0].FunctionResponse != null;
    }

    /// <summary>
    /// Port of convertGeminiToLLMResponse (outbound_convert.go:547-552).
    /// When isStream is true, it sets Delta instead of Message in choices.
    /// </summary>
    public static LlmResponse ConvertGeminiToLlmResponse(GenerateContentResponse geminiResp, bool isStream)
    {
        var (resp, _) = ConvertGeminiToLlmResponseWithState(geminiResp, isStream, 0);
        return resp;
    }

    /// <summary>Port of TransformerMetadataKeyGroundingMetadata (outbound_convert.go:555).</summary>
    public const string TransformerMetadataKeyGroundingMetadata = "gemini_grounding_metadata";

    /// <summary>Port of deriveGeminiAnnotations (outbound_convert.go:557-635).</summary>
    public static List<Annotation>? DeriveGeminiAnnotations(Candidate? candidate)
    {
        if (candidate == null)
        {
            return null;
        }

        if (candidate.CitationMetadata?.Citations is { Count: > 0 } citations)
        {
            var annotations = new List<Annotation>(citations.Count);
            foreach (var citation in citations)
            {
                if (citation == null)
                {
                    continue;
                }

                annotations.Add(new Annotation
                {
                    Type = "url_citation",
                    StartIndex = citation.StartIndex,
                    EndIndex = citation.EndIndex,
                    UrlCitation = new UrlCitation
                    {
                        Url = citation.URI,
                        Title = citation.Title,
                    },
                });
            }

            if (annotations.Count > 0)
            {
                return annotations;
            }
        }

        if (candidate.GroundingMetadata == null)
        {
            return null;
        }

        var groundingAnnotations = new List<Annotation>();
        foreach (var support in candidate.GroundingMetadata.GroundingSupports ?? [])
        {
            if (support?.Segment == null)
            {
                continue;
            }

            foreach (var idx in support.GroundingChunkIndices ?? [])
            {
                if (idx < 0 || idx >= (candidate.GroundingMetadata.GroundingChunks?.Count ?? 0))
                {
                    continue;
                }

                var chunk = candidate.GroundingMetadata.GroundingChunks![idx];
                if (chunk == null)
                {
                    continue;
                }

                string url;
                string title;
                if (chunk.Web is { } web)
                {
                    url = web.URI ?? string.Empty;
                    title = web.Title ?? string.Empty;
                }
                else if (chunk.RetrievedContext is { } retrieved)
                {
                    url = retrieved.URI ?? string.Empty;
                    title = retrieved.Title ?? string.Empty;
                }
                else
                {
                    continue;
                }

                groundingAnnotations.Add(new Annotation
                {
                    Type = "url_citation",
                    StartIndex = support.Segment.StartIndex,
                    EndIndex = support.Segment.EndIndex,
                    UrlCitation = new UrlCitation
                    {
                        Url = url,
                        Title = title,
                    },
                });
            }
        }

        return groundingAnnotations.Count == 0 ? null : groundingAnnotations;
    }

    /// <summary>
    /// Port of convertGeminiToLLMResponseWithState (outbound_convert.go:637-685):
    /// converts Gemini response with tool call index tracking. Returns the
    /// response and the next tool call index to use.
    /// </summary>
    public static (LlmResponse Response, int NextToolCallIndex) ConvertGeminiToLlmResponseWithState(
        GenerateContentResponse geminiResp,
        bool isStream,
        int toolCallIndexOffset)
    {
        var resp = new LlmResponse
        {
            Id = geminiResp.ResponseId ?? string.Empty,
            Model = geminiResp.ModelVersion ?? string.Empty,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RequestType = RequestTypes.Chat,
            ApiFormat = ApiFormats.GeminiContents,
        };

        // Set object type based on stream mode
        resp.Object = isStream ? "chat.completion.chunk" : "chat.completion";

        // Generate ID if not present. Go uuid.NewString() emits lowercase hex.
        if (resp.Id.Length == 0)
        {
            resp.Id = "chatcmpl-" + Guid.NewGuid().ToString("D").ToLowerInvariant();
        }

        // Convert candidates to choices
        var choices = new List<Choice>(geminiResp.Candidates?.Count ?? 0);
        var nextToolCallIndex = toolCallIndexOffset;

        foreach (var candidate in geminiResp.Candidates ?? [])
        {
            var (choice, next) = ConvertGeminiCandidateToLlmChoiceWithState(candidate, isStream, nextToolCallIndex);
            nextToolCallIndex = next;

            // Store GroundingMetadata in Choice.TransformerMetadata if present
            if (candidate.GroundingMetadata != null)
            {
                choice.TransformerMetadata ??= new Dictionary<string, JsonElement>();
                choice.TransformerMetadata[TransformerMetadataKeyGroundingMetadata] =
                    JsonSerializer.SerializeToElement(candidate.GroundingMetadata, LlmJson.Options);
            }

            choices.Add(choice);
        }

        resp.Choices = choices;
        resp.Usage = GeminiConvert.ConvertToLlmUsage(geminiResp.UsageMetadata);

        return (resp, nextToolCallIndex);
    }

    /// <summary>
    /// Port of convertGeminiCandidateToLLMChoiceWithState
    /// (outbound_convert.go:687-815): converts a Gemini Candidate to an LLM
    /// Choice with tool call index tracking.
    /// </summary>
    public static (Choice Choice, int NextToolCallIndex) ConvertGeminiCandidateToLlmChoiceWithState(
        Candidate candidate,
        bool isStream,
        int toolCallIndexOffset)
    {
        var choice = new Choice
        {
            Index = (int)candidate.Index,
        };

        var hasToolCall = false;

        var nextToolCallIndex = toolCallIndexOffset;

        if (candidate.Content != null)
        {
            var msg = new Message
            {
                Role = "assistant",
            };

            var textParts = new List<string>();
            var contentParts = new List<MessageContentPart>();
            var toolCalls = new List<ToolCall>();
            string reasoningContent = string.Empty;

            foreach (var part in candidate.Content.Parts ?? [])
            {
                if (msg.ReasoningSignature == null && part.ThoughtSignature is { Length: > 0 })
                {
                    msg.ReasoningSignature = GeminiSignatures.EncodeGeminiThoughtSignature(part.ThoughtSignature);
                }

                if (part.Text is { Length: > 0 } text)
                {
                    if (part.Thought)
                    {
                        reasoningContent = text;
                    }
                    else
                    {
                        textParts.Add(text);
                    }
                }
                else if (part.InlineData != null)
                {
                    // Convert inline data based on MIME type
                    var dataUrl = Xurl.BuildDataUrl(part.InlineData.MIMEType ?? string.Empty, part.InlineData.Data ?? string.Empty, true);

                    if (GeminiMime.IsDocumentMimeType(part.InlineData.MIMEType ?? string.Empty))
                    {
                        // Document type (PDF, Word, etc.)
                        contentParts.Add(new MessageContentPart
                        {
                            Type = "document",
                            Document = new DocumentUrl
                            {
                                Url = dataUrl,
                                MimeType = part.InlineData.MIMEType,
                            },
                        });
                    }
                    else
                    {
                        // Image type
                        contentParts.Add(new MessageContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new ImageUrl
                            {
                                Url = dataUrl,
                            },
                        });
                    }
                }
                else if (part.FunctionCall is { } functionCall)
                {
                    var argsJson = functionCall.Args is null
                        ? "null"
                        : GoJson.MarshalToString(functionCall.Args);
                    var tc = new ToolCall
                    {
                        Index = nextToolCallIndex,
                        Id = functionCall.Id ?? string.Empty,
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = functionCall.Name ?? string.Empty,
                            Arguments = argsJson,
                        },
                    };

                    // Gemini may response empty tool call ID.
                    if (tc.Id.Length == 0)
                    {
                        tc.Id = "tc_" + Guid.NewGuid().ToString("D").ToLowerInvariant();
                    }

                    GeminiThoughtSignatures.SetOutboundToolCallThoughtSignature(tc, part.ThoughtSignature);
                    toolCalls.Add(tc);
                    nextToolCallIndex++;
                }
            }

            // Set content - prefer multipart if we have images
            if (contentParts.Count > 0)
            {
                // Add text parts to content parts (Go prepends each text part,
                // which reverses their order)
                for (var i = textParts.Count - 1; i >= 0; i--)
                {
                    contentParts.Insert(0, new MessageContentPart
                    {
                        Type = "text",
                        Text = textParts[i],
                    });
                }

                msg.Content = new MessageContent
                {
                    MultipleContent = contentParts,
                };
            }
            else if (textParts.Count > 0)
            {
                msg.Content = new MessageContent
                {
                    Content = string.Join("", textParts),
                };
            }

            // Set tool calls
            if (toolCalls.Count > 0)
            {
                hasToolCall = true;
                msg.ToolCalls = toolCalls;
            }

            // Set reasoning content
            if (reasoningContent.Length > 0)
            {
                msg.ReasoningContent = reasoningContent;
            }

            if (DeriveGeminiAnnotations(candidate) is { Count: > 0 } annotations)
            {
                msg.Annotations = annotations;
            }

            // Set Delta for streaming, Message for non-streaming
            if (isStream)
            {
                choice.Delta = msg;
            }
            else
            {
                choice.Message = msg;
            }
        }

        // Convert finish reason
        choice.FinishReason = GeminiConvert.ConvertGeminiFinishReasonToLlm(candidate.FinishReason ?? string.Empty, hasToolCall);

        return (choice, nextToolCallIndex);
    }

    /// <summary>
    /// Port of extractJSONSchema (outbound_convert.go:817-829): extracts the
    /// inner "schema" field from an OpenAI json_schema object. OpenAI format:
    /// {"name": "...", "schema": {...}, "strict": ...}. Gemini expects the
    /// schema content directly without the wrapper.
    /// </summary>
    public static JsonElement ExtractJsonSchema(JsonElement raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw.GetRawText());
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema", out var schema)
                && schema.ValueKind != JsonValueKind.Null)
            {
                return schema.Clone();
            }
        }
        catch (JsonException)
        {
            // Go: an undecodable wrapper returns the raw message unchanged.
        }

        return raw;
    }
}
