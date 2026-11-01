using System.Text;
using System.Text.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>Reasoning field policy (llm/transformer/openai/outbound.go:30-42).</summary>
public enum ReasoningField
{
    Content,
    Reasoning,
    None,
    All,
}

/// <summary>
/// Port of llm/transformer/openai/outbound_convert.go — unified model to the
/// OpenAI wire model.
/// </summary>
public static class OpenAiConvertFromLlm
{
    public static OpenAiRequest? RequestFromLlm(this LlmRequest? r, ReasoningField reasoningField)
    {
        if (r is null)
        {
            return null;
        }

        var req = new OpenAiRequest
        {
            Model = r.Model,
            FrequencyPenalty = r.FrequencyPenalty,
            Logprobs = r.Logprobs,
            MaxCompletionTokens = r.MaxCompletionTokens,
            MaxTokens = r.MaxTokens,
            PresencePenalty = r.PresencePenalty,
            Seed = r.Seed,
            Store = r.Store,
            Temperature = r.Temperature,
            TopLogprobs = r.TopLogprobs,
            TopP = r.TopP,
            PromptCacheKey = r.PromptCacheKey,
            SafetyIdentifier = r.SafetyIdentifier,
            User = r.User,
            LogitBias = r.LogitBias,
            Metadata = r.Metadata,
            Modalities = r.Modalities,
            ReasoningEffort = GoStr(r.ReasoningEffort),
            ServiceTier = r.ServiceTier,
            Stream = r.Stream,
            ParallelToolCalls = r.ParallelToolCalls,
            Verbosity = r.Verbosity,
        };

        // Chat Completions accepts a single system message; merge the multiples
        // that some clients produce before mapping the rest.
        req.Messages = MergeSystemMessages(r.Messages.Select(m => m.MessageFromLlm(reasoningField)).ToList());

        if (r.Stop is not null)
        {
            req.Stop = new OpenAiStop
            {
                Single = r.Stop.Single,
                Multiple = r.Stop.Multiple,
            };
        }

        if (r.StreamOptions is not null)
        {
            req.StreamOptions = new OpenAiStreamOptions
            {
                IncludeUsage = r.StreamOptions.IncludeUsage,
            };
        }

        // Only function tools are representable in Chat Completions.
        var tools = (r.Tools ?? [])
            .Where(t => t.Type == "function")
            .Select(t => t.ToolFromLlm())
            .ToList();
        req.Tools = tools.Count == 0 ? null : tools;

        req.ToolChoice = r.ToolChoice.ToolChoiceFromLlm();

        if (r.ResponseFormat is not null)
        {
            req.ResponseFormat = new OpenAiResponseFormat
            {
                Type = r.ResponseFormat.Type,
                JsonSchema = r.ResponseFormat.JsonSchema,
            };
        }

        if (tools.Count == 0)
        {
            req.ParallelToolCalls = null;
        }

        return req;
    }

    /// <summary>
    /// Collapses all system-role messages into one at the position of the first,
    /// dropping the rest (llm/transformer/openai/outbound_convert.go:110-149).
    /// </summary>
    public static List<OpenAiMessage> MergeSystemMessages(List<OpenAiMessage> msgs)
    {
        var systemCount = 0;
        var firstSystem = -1;
        var systemText = new StringBuilder();

        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (m.Role != "system")
            {
                continue;
            }

            if (firstSystem == -1)
            {
                firstSystem = i;
            }

            systemCount++;
            foreach (var text in MessageTextParts(m))
            {
                if (systemText.Length > 0)
                {
                    systemText.Append("\n\n");
                }

                systemText.Append(text);
            }
        }

        if (systemCount == 0)
        {
            return msgs;
        }

        var merged = new List<OpenAiMessage>(msgs.Count - systemCount + 1);
        var mergedSystem = msgs[firstSystem];
        if (systemCount > 1)
        {
            mergedSystem.Content = new OpenAiMessageContent { Content = systemText.ToString() };
        }

        merged.Add(mergedSystem);
        foreach (var m in msgs)
        {
            if (m.Role != "system")
            {
                merged.Add(m);
            }
        }

        return merged;
    }

    /// <summary>
    /// Text segments of a message; MultipleContent takes precedence over the
    /// scalar Content, matching the reference.
    /// </summary>
    private static IEnumerable<string> MessageTextParts(OpenAiMessage m)
    {
        if (m.Content?.MultipleContent is { Count: > 0 } parts)
        {
            return parts
                .Where(p => p.Type == "text" && p.Text is not null)
                .Select(p => p.Text!);
        }

        if (m.Content?.Content is { } content)
        {
            return [content];
        }

        return [];
    }

    public static OpenAiMessage MessageFromLlm(this Message m, ReasoningField reasoningField)
    {
        string? reasoningContent;
        string? reasoning;

        switch (reasoningField)
        {
            case ReasoningField.Content:
                reasoningContent = m.ReasoningContent ?? m.Reasoning;
                reasoning = null;
                break;
            case ReasoningField.Reasoning:
                reasoning = m.Reasoning ?? m.ReasoningContent;
                reasoningContent = null;
                break;
            case ReasoningField.None:
                reasoningContent = null;
                reasoning = null;
                break;
            default:
                reasoningContent = m.ReasoningContent;
                reasoning = m.Reasoning;

                if (reasoningContent is null && reasoning is { Length: > 0 })
                {
                    reasoningContent = reasoning;
                }

                if (reasoning is null && reasoningContent is { Length: > 0 })
                {
                    reasoning = reasoningContent;
                }

                break;
        }

        var msg = new OpenAiMessage
        {
            Role = GoStr(m.Role),
            Name = m.Name,
            Refusal = GoStr(m.Refusal),
            ToolCallId = m.ToolCallId,
            ReasoningContent = reasoningContent,
            Reasoning = reasoning,
        };

        if (m.Audio is not null)
        {
            msg.Audio = new OpenAiOutputAudio
            {
                Id = GoStr(m.Audio.Id),
                Data = GoStr(m.Audio.Data),
                ExpiresAt = m.Audio.ExpiresAt,
                Transcript = GoStr(m.Audio.Transcript),
            };
        }

        msg.Content = MessageContentFromLlm(m.Content);

        if (m.ToolCalls is not null)
        {
            msg.ToolCalls = m.ToolCalls.Count == 0
                ? null
                : m.ToolCalls.Select(tc => tc.ToolCallFromLlm()).ToList();
        }

        // An assistant turn may carry only reasoning or only tool calls; a
        // message whose parts were all filtered out is left with an empty part
        // list. Normalize to an empty string, which every upstream accepts.
        var hasContent = msg.Content?.Content is not null || msg.Content?.MultipleContent is { Count: > 0 };
        if (!hasContent && (msg.Role == "assistant" || msg.ToolCalls is { Count: > 0 }))
        {
            msg.Content = new OpenAiMessageContent { Content = string.Empty };
        }

        if (m.Annotations is { Count: > 0 })
        {
            msg.Annotations = m.Annotations.Select(a => a.AnnotationFromLlm()).ToList();
        }

        return msg;
    }

    public static OpenAiAnnotation AnnotationFromLlm(this Annotation a)
    {
        var annotation = new OpenAiAnnotation
        {
            Type = GoStr(a.Type),
            StartIndex = a.StartIndex,
            EndIndex = a.EndIndex,
        };

        if (a.UrlCitation is not null)
        {
            annotation.UrlCitation = new OpenAiUrlCitation
            {
                Url = GoStr(a.UrlCitation.Url),
                Title = GoStr(a.UrlCitation.Title),
            };
        }

        return annotation;
    }

    /// <summary>
    /// Maps unified content to the wire shape, dropping compaction parts. The
    /// result is null when the reference would omit the field entirely
    /// (<c>content,omitzero</c> with both representations absent).
    /// </summary>
    public static OpenAiMessageContent? MessageContentFromLlm(MessageContent? c)
    {
        if (c is null)
        {
            return null;
        }

        var content = new OpenAiMessageContent
        {
            Content = c.Content,
        };

        if (c.MultipleContent is not null)
        {
            content.MultipleContent = c.MultipleContent
                .Where(p => p.Type is not ("compaction" or "compaction_summary"))
                .Select(p => p.MessageContentPartFromLlm())
                .ToList();
        }

        // Go's omitzero compares against the zero value: a non-nil (even empty)
        // part list is not zero and is emitted (as null by MarshalJSON).
        if (content.Content is null && content.MultipleContent is null)
        {
            return null;
        }

        return content;
    }

    public static OpenAiMessageContentPart MessageContentPartFromLlm(this MessageContentPart p)
    {
        var part = new OpenAiMessageContentPart
        {
            Type = NormalizeContentPartType(p.Type),
            Text = p.Text,
        };

        if (p.ImageUrl is not null)
        {
            part.ImageUrl = new OpenAiImageUrl
            {
                Url = p.ImageUrl.Url,
                Detail = p.ImageUrl.Detail,
            };
        }

        if (p.VideoUrl is not null)
        {
            part.VideoUrl = new OpenAiVideoUrl
            {
                Url = p.VideoUrl.URL,
            };
        }

        if (p.InputAudio is not null)
        {
            part.InputAudio = new OpenAiInputAudio
            {
                Format = p.InputAudio.Format,
                Data = p.InputAudio.Data,
            };
        }

        if (p.Document is not null)
        {
            part.Type = "file";
            part.File = new OpenAiFile
            {
                FileId = GoStr(p.Document.FileId),
                Filename = GoStr(p.Document.Filename),
            };

            if (p.Document.Url is { } url && url.StartsWith("data:", StringComparison.Ordinal))
            {
                part.File.FileData = url;
            }

            if (string.IsNullOrEmpty(part.File.Filename) && p.Document.MimeType == "application/pdf")
            {
                part.File.Filename = "document.pdf";
            }
        }

        return part;
    }

    /// <summary>Responses-only text types collapse to Chat Completions "text".</summary>
    private static string NormalizeContentPartType(string partType) => partType switch
    {
        "input_text" or "output_text" => "text",
        _ => partType,
    };

    public static OpenAiToolChoice? ToolChoiceFromLlm(this ToolChoice? tc)
    {
        if (tc is null)
        {
            return null;
        }

        var choice = new OpenAiToolChoice { ToolChoice = tc.ToolChoiceMode };

        if (tc.NamedToolChoice is not null)
        {
            choice.NamedToolChoice = new OpenAiNamedToolChoice
            {
                Type = tc.NamedToolChoice.Type,
                Function = new OpenAiToolFunction { Name = tc.NamedToolChoice.Function.Name },
            };

            // The plain named shape would emit an empty function name and
            // silently lift the caller's restriction.
            if (tc.NamedToolChoice.Type == "allowed_tools")
            {
                choice.ToolChoice = null;
                choice.AllowedTools = new OpenAiAllowedTools
                {
                    Mode = tc.ToolChoiceMode,
                    Tools = tc.Tools is { Count: > 0 }
                        ? tc.Tools.Select(o => new OpenAiNamedToolChoice
                        {
                            Type = o.Type,
                            Function = new OpenAiToolFunction { Name = o.Name },
                        }).ToList()
                        : null,
                };
            }
        }

        return choice;
    }

    /// <summary>Go's <c>string</c> + <c>omitempty</c>: the empty string is omitted.</summary>
    private static string? GoStr(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public static OpenAiTool ToolFromLlm(this Tool t) => new()
    {
        Type = t.Type,
        Function = new OpenAiFunction
        {
            Name = t.Function.Name,
            Description = GoStr(t.Function.Description),
            Parameters = t.Function.Parameters,
            Strict = t.Function.Strict,
        },
    };

    public static OpenAiToolCall ToolCallFromLlm(this ToolCall tc)
    {
        var toolCall = new OpenAiToolCall
        {
            Id = GoStr(tc.Id),
            Type = GoStr(tc.Type),
            Function = new OpenAiFunctionCall
            {
                Name = GoStr(tc.Function.Name),
                Arguments = tc.Function.Arguments,
            },
            Index = tc.Index,
        };

        if (tc.TransformerMetadata is not null
            && tc.TransformerMetadata.TryGetValue(OpenAiConvertToLlm.TransformerMetadataKeyGoogleThoughtSignature, out var raw)
            && raw.ValueKind == JsonValueKind.String
            && raw.GetString() is { Length: > 0 } signature)
        {
            toolCall.ExtraContent = new OpenAiToolCallExtraContent
            {
                Google = new OpenAiToolCallGoogleExtraContent
                {
                    ThoughtSignature = signature,
                },
            };
        }

        return toolCall;
    }

    /// <summary>Port of openai.ResponseFromLLM.</summary>
    public static OpenAiResponse? ResponseFromLlm(LlmResponse? r)
    {
        if (r is null)
        {
            return null;
        }

        var resp = new OpenAiResponse
        {
            Id = r.Id,
            Object = r.Object,
            Created = r.Created,
            Model = r.Model,
            SystemFingerprint = GoStr(r.SystemFingerprint),
            ServiceTier = GoStr(r.ServiceTier),
            Choices = r.Choices.Select(ChoiceFromLlm).ToList(),
        };

        if (r.Usage is not null)
        {
            resp.Usage = OpenAiUsageConvert.FromLlm(r.Usage);
        }

        if (r.Error is not null)
        {
            resp.Error = new OpenAiError
            {
                StatusCode = r.Error.StatusCode,
                Detail = r.Error.Detail,
            };
        }

        if (r.TransformerMetadata is not null
            && r.TransformerMetadata.TryGetValue(OpenAiConvertToLlm.TransformerMetadataKeyCitations, out var citations)
            && citations.ValueKind == JsonValueKind.Array)
        {
            var list = citations.Deserialize<List<string>>();
            if (list is { Count: > 0 })
            {
                resp.Citations = list;
            }
        }

        return resp;
    }

    /// <summary>Port of openai.ChoiceFromLLM (uses the "all" reasoning policy).</summary>
    public static OpenAiChoice ChoiceFromLlm(Choice c)
    {
        var choice = new OpenAiChoice
        {
            Index = c.Index,
            FinishReason = c.FinishReason,
        };

        if (c.Message is not null)
        {
            choice.Message = c.Message.MessageFromLlm(ReasoningField.All);
        }

        if (c.Delta is not null)
        {
            choice.Delta = c.Delta.MessageFromLlm(ReasoningField.All);
        }

        if (c.Logprobs is not null)
        {
            choice.Logprobs = new OpenAiLogprobs
            {
                Content = c.Logprobs.Content.Select(t => new OpenAiTokenLogprob
                {
                    Token = t.Token,
                    Logprob = t.Logprob,
                    Bytes = t.Bytes is { Count: > 0 } bytes ? bytes : null,
                    TopLogprobs = t.TopLogprobs is { Count: > 0 } top
                        ? top.Select(tl => new OpenAiTopLogprob
                        {
                            Token = tl.Token,
                            Logprob = tl.Logprob,
                            Bytes = tl.Bytes is { Count: > 0 } tbytes ? tbytes : null,
                        }).ToList()
                        : null,
                }).ToList(),
            };
        }

        return choice;
    }

    /// <summary>
    /// Strips provider extension blocks that the OpenAI platform rejects
    /// (llm/transformer/openai/google.go:14-48).
    /// </summary>
    public static void StripUnsupportedToolCallExtraContent(OpenAiRequest? req)
    {
        if (req?.Messages is null)
        {
            return;
        }

        foreach (var message in req.Messages)
        {
            if (message.ToolCalls is null)
            {
                continue;
            }

            foreach (var tc in message.ToolCalls)
            {
                if (tc.ExtraContent is not null
                    && (tc.ExtraContent.Google is null || !string.IsNullOrEmpty(tc.ExtraContent.Google.ThoughtSignature)))
                {
                    tc.ExtraContent = null;
                }

                if (tc.ExtraFields is not null
                    && (tc.ExtraFields.ExtraContent is null
                        || tc.ExtraFields.ExtraContent.Google is null
                        || !string.IsNullOrEmpty(tc.ExtraFields.ExtraContent.Google.ThoughtSignature)))
                {
                    tc.ExtraFields = null;
                }
            }
        }
    }
}
