using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>Port of the shared conversion helpers (llm/transformer/gemini/convert.go).</summary>
public static class GeminiConvert
{
    /// <summary>Port of extractTextFromContent (convert.go:12-26).</summary>
    public static string ExtractTextFromContent(Content? content)
    {
        if (content?.Parts is not { Count: > 0 } parts)
        {
            return "";
        }

        var texts = new List<string>();
        foreach (var part in parts)
        {
            if (part.Text is { Length: > 0 } text && !part.Thought)
            {
                texts.Add(text);
            }
        }

        return string.Join("\n", texts);
    }

    /// <summary>Port of extractPartsFromLLMMessage (convert.go:28-42).</summary>
    public static List<Part> ExtractPartsFromLlmMessage(Message msg)
    {
        if (msg.Content?.Content is { Length: > 0 } content)
        {
            return [new Part { Text = content }];
        }

        var parts = new List<Part>();
        if (msg.Content?.MultipleContent is { } multiple)
        {
            foreach (var part in multiple)
            {
                if (part.Type == "text" && part.Text is { Length: > 0 } text)
                {
                    parts.Add(new Part { Text = text });
                }
            }
        }

        return parts;
    }

    /// <summary>Port of convertGeminiRoleToLLMRole (convert.go:44-55).</summary>
    public static string ConvertGeminiRoleToLlmRole(string role) => role switch
    {
        "model" => "assistant",
        "user" => "user",
        "" => "user",
        _ => role,
    };

    /// <summary>Port of convertLLMRoleToGeminiRole (convert.go:57-70).</summary>
    public static string ConvertLlmRoleToGeminiRole(string role) => role switch
    {
        "assistant" => "model",
        "user" => "user",
        // Developer role is treated as system-equivalent and handled in system
        // instruction; convert to user for content processed as regular content.
        "developer" => "user",
        _ => role,
    };

    /// <summary>Port of convertGeminiFinishReasonToLLM (convert.go:94-118).</summary>
    public static string? ConvertGeminiFinishReasonToLlm(string reason, bool hasToolCall)
    {
        if (reason == "")
        {
            return null;
        }

        var llmReason = reason switch
        {
            "STOP" => hasToolCall ? "tool_calls" : "stop",
            "MAX_TOKENS" => "length",
            "SAFETY" => "content_filter",
            "RECITATION" => "content_filter",
            _ => "stop",
        };

        return llmReason;
    }

    /// <summary>Port of convertLLMFinishReasonToGemini (convert.go:120-137).</summary>
    public static string ConvertLlmFinishReasonToGemini(string? reason)
    {
        if (reason is null)
        {
            return "";
        }

        return reason switch
        {
            "stop" => "STOP",
            "length" => "MAX_TOKENS",
            "content_filter" => "SAFETY",
            "tool_calls" => "STOP",
            _ => "STOP",
        };
    }

    /// <summary>Port of convertImageURLToGeminiPart (convert.go:139-159).</summary>
    public static Part? ConvertImageUrlToGeminiPart(ImageUrl? image)
    {
        if (image is null || image.Url.Length == 0)
        {
            return null;
        }

        if (Xurl.ParseDataUrl(image.Url) is { } parsed)
        {
            return new Part
            {
                InlineData = new Blob
                {
                    MIMEType = parsed.MediaType,
                    Data = parsed.Data,
                },
            };
        }

        return new Part
        {
            FileData = new FileData
            {
                FileURI = image.Url,
                MIMEType = GeminiMime.ImageMimeType(image),
            },
        };
    }

    /// <summary>Port of convertVideoURLToGeminiPart (convert.go:161-181).</summary>
    public static Part? ConvertVideoUrlToGeminiPart(VideoURL? video)
    {
        if (video is null || video.URL.Length == 0)
        {
            return null;
        }

        if (Xurl.ParseDataUrl(video.URL) is { } parsed)
        {
            return new Part
            {
                InlineData = new Blob
                {
                    MIMEType = parsed.MediaType,
                    Data = parsed.Data,
                },
            };
        }

        return new Part
        {
            FileData = new FileData
            {
                FileURI = video.URL,
                MIMEType = GeminiMime.MediaMimeType(video.MIMEType, video.URL, GeminiMime.IsVideoMimeType),
            },
        };
    }

    /// <summary>Port of convertAudioToGeminiPart (convert.go:183-212).</summary>
    public static Part? ConvertAudioToGeminiPart(InputAudio? audio)
    {
        if (audio is null)
        {
            return null;
        }

        if (audio.Data.Length > 0)
        {
            var mimeType = audio.MimeType;
            if (mimeType is null or "")
            {
                mimeType = AudioFormatToMimeType(audio.Format);
            }

            return new Part
            {
                InlineData = new Blob
                {
                    MIMEType = mimeType,
                    Data = audio.Data,
                },
            };
        }

        if (audio.Url is null or "")
        {
            return null;
        }

        return new Part
        {
            FileData = new FileData
            {
                FileURI = audio.Url,
                MIMEType = GeminiMime.MediaMimeType(audio.MimeType, audio.Url, GeminiMime.IsAudioMimeType),
            },
        };
    }

    /// <summary>
    /// Port of convertDocumentURLToGeminiPart (convert.go:216-242): handles
    /// both data URLs and regular URLs for documents (PDF, Word, etc.).
    /// </summary>
    public static Part? ConvertDocumentUrlToGeminiPart(DocumentUrl? doc)
    {
        if (doc is null || doc.Url.Length == 0)
        {
            return null;
        }

        if (Xurl.ParseDataUrl(doc.Url) is { } parsed)
        {
            var mimeType = parsed.MediaType;
            if (mimeType == "" && doc.MimeType is { Length: > 0 } declared)
            {
                mimeType = declared;
            }

            return new Part
            {
                InlineData = new Blob
                {
                    MIMEType = mimeType,
                    Data = parsed.Data,
                },
            };
        }

        return new Part
        {
            FileData = new FileData
            {
                FileURI = doc.Url,
                MIMEType = GeminiMime.MediaMimeType(doc.MimeType, doc.Url, GeminiMime.IsDocumentMimeType),
            },
        };
    }

    /// <summary>Port of audioFormatToMIMEType (convert.go:276-297).</summary>
    public static string AudioFormatToMimeType(string format) => format.ToLowerInvariant() switch
    {
        "wav" or "wave" or "x-wav" => "audio/wav",
        "mpeg" or "mpga" => "audio/mpeg",
        "webm" => "audio/webm",
        "ogg" => "audio/ogg",
        "flac" => "audio/flac",
        "aac" => "audio/aac",
        "mp4" or "m4a" => "audio/mp4",
        _ => "audio/mp3",
    };

    /// <summary>Port of audioMIMETypeToFormat (convert.go:299-318).</summary>
    public static string AudioMimeTypeToFormat(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
        "audio/mpeg" or "audio/mpga" => "mp3",
        "audio/webm" => "webm",
        "audio/ogg" => "ogg",
        "audio/flac" => "flac",
        "audio/aac" => "aac",
        "audio/mp4" or "audio/m4a" => "mp4",
        _ => "mp3",
    };

    /// <summary>Port of convertGeminiFunctionCallingConfigToToolChoice (convert.go:320-354).</summary>
    public static ToolChoice? ConvertGeminiFunctionCallingConfigToToolChoice(FunctionCallingConfig? fcc)
    {
        if (fcc is null)
        {
            return null;
        }

        var tc = new ToolChoice();

        switch (fcc.Mode)
        {
            case "AUTO":
                tc.ToolChoiceMode = "auto";
                break;
            case "ANY":
                if (fcc.AllowedFunctionNames is { Count: 1 })
                {
                    tc.NamedToolChoice = new NamedToolChoice
                    {
                        Type = "function",
                        Function = new ToolFunction { Name = fcc.AllowedFunctionNames[0] },
                    };
                }
                else
                {
                    tc.ToolChoiceMode = "required";
                    if (fcc.AllowedFunctionNames is { Count: > 1 } names)
                    {
                        tc.NamedToolChoice = new NamedToolChoice { Type = "allowed_tools" };
                        foreach (var name in names)
                        {
                            tc.Tools ??= new List<ToolOption>();
                            tc.Tools.Add(new ToolOption { Type = ToolTypes.Function, Name = name });
                        }
                    }
                }

                break;
            case "NONE":
                tc.ToolChoiceMode = "none";
                break;
            default:
                tc.ToolChoiceMode = "auto";
                break;
        }

        return tc;
    }

    /// <summary>Port of convertLLMToolChoiceToGeminiToolConfig (convert.go:356-394).</summary>
    public static ToolConfig? ConvertLlmToolChoiceToGeminiToolConfig(ToolChoice? tc)
    {
        if (tc is null)
        {
            return null;
        }

        var fcc = new FunctionCallingConfig();

        if (tc.ToolChoiceMode is { } mode)
        {
            fcc.Mode = mode switch
            {
                "auto" => "AUTO",
                "none" => "NONE",
                "required" or "any" => "ANY",
                _ => "AUTO",
            };
        }
        else if (tc.NamedToolChoice is { } named)
        {
            // Named tool choice - specific function
            fcc.Mode = "ANY";
            if (named.Function.Name.Length > 0)
            {
                fcc.AllowedFunctionNames = [named.Function.Name];
            }
        }
        else
        {
            fcc.Mode = "AUTO";
        }

        if (fcc.Mode == "ANY" && tc.Tools is { } tools)
        {
            foreach (var tool in tools)
            {
                if (tool.Type == ToolTypes.Function)
                {
                    fcc.AllowedFunctionNames ??= new List<string>();
                    fcc.AllowedFunctionNames.Add(tool.Name);
                }
            }
        }

        return new ToolConfig { FunctionCallingConfig = fcc };
    }

    /// <summary>Port of thinkingBudgetToReasoningEffort (convert.go:396-405).</summary>
    public static string ThinkingBudgetToReasoningEffort(long budget)
    {
        if (budget <= 1024)
        {
            return "low";
        }

        return budget <= 8192 ? "medium" : "high";
    }

    /// <summary>
    /// Port of shouldUseThinkingLevelForBudget (convert.go:410-425): Gemini 3
    /// prefers thinkingLevel over thinkingBudget for standard budgets.
    /// </summary>
    public static bool ShouldUseThinkingLevelForBudget(string model, long budget)
    {
        if (!model.ToLowerInvariant().Contains("gemini-3-", StringComparison.Ordinal))
        {
            return false;
        }

        // Go switch: <=1024 true, <=8192 true, 1024..=32768 true, else false.
        return budget <= 32768;
    }

    /// <summary>Port of defaultGeminiReasoningEffortMapping (convert.go:428-433).</summary>
    private static readonly Dictionary<string, long> DefaultGeminiReasoningEffortMapping =
        new(StringComparer.Ordinal)
        {
            ["low"] = 1024,
            ["medium"] = 8192,
            ["high"] = 32768,
            ["xhigh"] = 32768,
        };

    /// <summary>Port of reasoningEffortToThinkingBudget (convert.go:435-437).</summary>
    public static long ReasoningEffortToThinkingBudget(string effort)
        => ReasoningEffortToThinkingBudgetWithConfig(effort, null);

    /// <summary>Port of reasoningEffortToThinkingBudgetWithConfig (convert.go:440-454).</summary>
    public static long ReasoningEffortToThinkingBudgetWithConfig(string effort, GeminiOutboundConfig? config)
    {
        if (config?.ReasoningEffortToBudget is { } custom
            && custom.TryGetValue(effort, out var customBudget))
        {
            return customBudget;
        }

        if (DefaultGeminiReasoningEffortMapping.TryGetValue(effort, out var budget))
        {
            return budget;
        }

        // Default to medium if not found.
        return 8192;
    }

    /// <summary>Port of convertToLLMUsage (convert.go:457-501).</summary>
    public static Usage? ConvertToLlmUsage(UsageMetadata? geminiUsage)
    {
        if (geminiUsage is null)
        {
            return null;
        }

        var usage = new Usage
        {
            PromptTokens = geminiUsage.PromptTokenCount,
            CompletionTokens = geminiUsage.CandidatesTokenCount + geminiUsage.ThoughtsTokenCount,
            TotalTokens = geminiUsage.TotalTokenCount,
        };

        if (geminiUsage.CachedContentTokenCount > 0)
        {
            usage.PromptTokensDetails = new PromptTokensDetails
            {
                CachedTokens = geminiUsage.CachedContentTokenCount,
            };
        }

        if (geminiUsage.ThoughtsTokenCount > 0)
        {
            usage.CompletionTokensDetails = new CompletionTokensDetails
            {
                ReasoningTokens = geminiUsage.ThoughtsTokenCount,
            };
        }

        if (geminiUsage.CandidatesTokensDetails is { Count: > 0 } candidates)
        {
            // llm.ModalityTokenCount, qualified like the Go reference's import.
            usage.CompletionModalityTokenDetails = candidates
                .Select(static candidate => new AxonHub.Llm.ModalityTokenCount
                {
                    Modality = candidate.Modality,
                    TokenCount = candidate.TokenCount,
                })
                .ToList();
        }

        if (geminiUsage.PromptTokensDetails is { Count: > 0 } prompt)
        {
            usage.PromptModalityTokenDetails = prompt
                .Select(static candidate => new AxonHub.Llm.ModalityTokenCount
                {
                    Modality = candidate.Modality,
                    TokenCount = candidate.TokenCount,
                })
                .ToList();
        }

        return usage;
    }

    /// <summary>
    /// Port of convertLLMModalitiesToGemini (convert.go:506-510): LLM uses
    /// lowercase ("text", "image", "audio"), Gemini uppercase.
    /// </summary>
    public static List<string> ConvertLlmModalitiesToGemini(List<string> modalities)
        => modalities.Select(static m => m.ToUpperInvariant()).ToList();

    /// <summary>Port of convertGeminiModalitiesToLLM (convert.go:515-519).</summary>
    public static List<string> ConvertGeminiModalitiesToLlm(List<string> modalities)
        => modalities.Select(static m => m.ToLowerInvariant()).ToList();

    /// <summary>Port of convertToGeminiUsage (convert.go:521-564).</summary>
    public static UsageMetadata? ConvertToGeminiUsage(Usage? chatUsage)
    {
        if (chatUsage is null)
        {
            return null;
        }

        var usage = new UsageMetadata
        {
            PromptTokenCount = chatUsage.PromptTokens,
            CandidatesTokenCount = chatUsage.CompletionTokens,
            TotalTokenCount = chatUsage.TotalTokens,
            CachedContentTokenCount = 0,
            ThoughtsTokenCount = 0,
            CandidatesTokensDetails = null,
            Cost = chatUsage.Cost,
        };

        if (chatUsage.PromptTokensDetails is { } promptDetails)
        {
            usage.CachedContentTokenCount = promptDetails.CachedTokens;
        }

        if (chatUsage.CompletionTokensDetails is { } completionDetails)
        {
            usage.ThoughtsTokenCount = completionDetails.ReasoningTokens;
            usage.CandidatesTokenCount = chatUsage.CompletionTokens - usage.ThoughtsTokenCount;
        }

        if (chatUsage.CompletionModalityTokenDetails is { Count: > 0 } completionModalities)
        {
            usage.CandidatesTokensDetails = completionModalities
                .Select(static modalityToken => new ModalityTokenCount
                {
                    Modality = modalityToken.Modality,
                    TokenCount = modalityToken.TokenCount,
                })
                .ToList();
        }

        if (chatUsage.PromptModalityTokenDetails is { Count: > 0 } promptModalities)
        {
            usage.PromptTokensDetails = promptModalities
                .Select(static modalityToken => new ModalityTokenCount
                {
                    Modality = modalityToken.Modality,
                    TokenCount = modalityToken.TokenCount,
                })
                .ToList();
        }

        return usage;
    }

    /// <summary>Port of TransformerMetadataKeySafetySettings (convert.go:567).</summary>
    public const string TransformerMetadataKeySafetySettings = "gemini_safety_settings";

    /// <summary>
    /// Port of extractSafetySettingsFromMetadata (convert.go:570-582). The Go
    /// type assertion yields nil for a missing key, an empty slice, or any
    /// stored value that is not a []*SafetySetting.
    /// </summary>
    public static List<SafetySetting>? ExtractSafetySettingsFromMetadata(
        Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        if (!metadata.TryGetValue(TransformerMetadataKeySafetySettings, out var rawSettings))
        {
            return null;
        }

        try
        {
            var settings = LlmJson.Deserialize<List<SafetySetting>>(rawSettings.GetRawText());
            return settings is { Count: > 0 } ? settings : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Port of TransformerMetadataKeyImageConfig (convert.go:585).</summary>
    public const string TransformerMetadataKeyImageConfig = "gemini_image_config";

    /// <summary>
    /// Port of extractImageConfigFromMetadata (convert.go:588-600). A stored
    /// value that is not an ImageConfig (e.g. a JSON string) yields nil like
    /// the Go type assertion.
    /// </summary>
    public static ImageConfig? ExtractImageConfigFromMetadata(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        if (!metadata.TryGetValue(TransformerMetadataKeyImageConfig, out var rawConfig))
        {
            return null;
        }

        try
        {
            return LlmJson.Deserialize<ImageConfig>(rawConfig.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
